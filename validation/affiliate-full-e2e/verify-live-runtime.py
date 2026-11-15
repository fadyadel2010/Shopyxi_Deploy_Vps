"""Fresh read-only Kubernetes/CRI checks. No secrets, configuration, or business data."""
import json
from pathlib import Path
import subprocess
import sys

REMOTE = r'''
import datetime,json,subprocess
def call(*args): return subprocess.check_output(args,text=True,timeout=40)
def graph(digest,seen):
    assert len(seen)<16
    if digest in seen: return []
    seen.add(digest)
    item=json.loads(call('/usr/local/bin/k3s','ctr','content','get',digest))
    if 'manifests' in item:
        return [leaf for child in item['manifests'] for leaf in graph(child['digest'],seen)]
    return [item]
expected={'api':'880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3',
    'web':'f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',
    'storefront':'e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df'}
rows=[]
for kind,digest in expected.items():
    name='shopixy-'+kind
    deployment=json.loads(call('/usr/local/bin/kubectl','get','deployment',name,'-n','shopixy-app','-o','json'))
    containers=deployment['spec']['template']['spec']['containers']
    image=next(c['image'] for c in containers if c['name']==kind)
    assert image=='docker.io/shopixy/'+kind+'@sha256:'+digest, 'Live image differs; stop baseline admission'
    selector=','.join(k+'='+v for k,v in deployment['spec']['selector']['matchLabels'].items())
    pods=json.loads(call('/usr/local/bin/kubectl','get','pods','-n','shopixy-app','-l',selector,'-o','json'))
    observations=[]
    inspection=json.loads(call('/usr/local/bin/k3s','crictl','inspecti',image))
    source=graph('sha256:'+digest,set())
    for pod in pods['items']:
        if pod['metadata'].get('deletionTimestamp'): continue
        status=next(c for c in pod['status']['containerStatuses'] if c['name']==kind)
        seen=set(); runtime=graph(status['imageID'].split('@')[-1],seen)
        selected=[m for m in runtime if m.get('config',{}).get('digest')==inspection['status']['id']]
        parity=len(selected)==1 and any(m.get('config')==selected[0]['config'] and m.get('layers')==selected[0]['layers'] for m in source)
        assert 'sha256:'+digest in seen and parity and status['ready']
        observations.append({'name':pod['metadata']['name'],'imageID':status['imageID'],
            'ready':status['ready'],'restartCount':status['restartCount'],'configAndLayersMatchPinnedImage':parity})
    status=deployment['status']; generation=deployment['metadata']['generation']
    assert status['observedGeneration']==generation and status.get('readyReplicas',0)==deployment['spec']['replicas']
    assert observations
    rows.append({'component':kind,'image':image,'generation':generation,'observedGeneration':status['observedGeneration'],
        'desiredReplicas':deployment['spec']['replicas'],'readyReplicas':status.get('readyReplicas',0),
        'availableReplicas':status.get('availableReplicas',0),'runtimeConfigId':inspection['status']['id'],'pods':observations})
print(json.dumps({'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'method':'Fresh kubectl GET and containerd/CRI content inspection, not a previous deployment receipt',
    'productionMutations':False,'workloads':rows},indent=2))
'''


def main():
    remote = REMOTE
    filename = 'production-runtime-current.json'
    if sys.argv[1:] == ['--after-api-release']:
        candidate = json.loads((Path(__file__).parent / 'production-api-candidate.json').read_text())
        new_digest = candidate['image'].split('@sha256:')[1]
        remote = remote.replace('880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3', new_digest)
        filename = 'production-runtime-after-api-release.json'
    else:
        assert not sys.argv[1:]
    result = subprocess.run(['ssh', '-o', 'BatchMode=yes', 'shopixy-vps', 'sudo python3 -'],
        input=remote, text=True, capture_output=True, timeout=180)
    if result.returncode:
        raise SystemExit('Fresh read-only runtime verification failed; raw output withheld')
    data = json.loads(result.stdout)
    (Path(__file__).parent / filename).write_text(json.dumps(data, indent=2) + '\n')
    print(json.dumps(data, indent=2))


if __name__ == '__main__':
    main()
