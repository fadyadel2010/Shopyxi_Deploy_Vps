"""Run on the existing single Production node with the reviewed candidate artifacts.
Only imports the verified API image and conditionally replaces that deployment image.
No schema, grants, configuration, Web, Storefront, identities or business writes.
"""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

ROOT = Path('/var/lib/shopixy/affiliate-full-e2e-20261115')
OLD = {
    'api': 'docker.io/shopixy/api@sha256:880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3',
    'web': 'docker.io/shopixy/web@sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',
    'storefront': 'docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df'}
GENERATIONS = {'api': 47, 'web': 15, 'storefront': 10}

def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat()
def sha(value): return hashlib.sha256(value).hexdigest()
def digest(value): return sha(json.dumps(value, sort_keys=True, separators=(',', ':')).encode())
def call(*args, timeout=180):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        (ROOT/'last-command-error-private.log').write_text(result.stderr)
        raise RuntimeError('Release command failed; raw output withheld')
    return result.stdout
def kube(*args, timeout=180): return call('/usr/local/bin/kubectl', '--request-timeout=30s', *args, timeout=timeout)

def deployment(kind):
    d = json.loads(kube('get', 'deployment', 'shopixy-'+kind, '-n', 'shopixy-app', '-o', 'json'))
    spec = d['spec']
    index = next(i for i,c in enumerate(spec['template']['spec']['containers']) if c['name'] == kind)
    selector = ','.join(k+'='+v for k,v in spec['selector']['matchLabels'].items())
    pods = [p for p in json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items']
        if not p['metadata'].get('deletionTimestamp')]
    safe = {'uid':d['metadata']['uid'], 'resourceVersion':d['metadata']['resourceVersion'],
        'generation':d['metadata']['generation'], 'observedGeneration':d.get('status',{}).get('observedGeneration'),
        'index':index, 'image':spec['template']['spec']['containers'][index]['image'], 'specSha256':digest(spec),
        'replicas':spec.get('replicas'),
        **{key:d.get('status',{}).get(key,0) for key in ('readyReplicas','updatedReplicas','availableReplicas')},
        'pods':[{'name':p['metadata']['name'], 'containers':[{key:c.get(key) for key in ('name','imageID','ready','restartCount')}
            for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}
    return safe, spec, pods

def ready(s):
    return s['replicas'] == s['readyReplicas'] == s['updatedReplicas'] == s['availableReplicas'] == 1 and \
        s['generation'] == s['observedGeneration'] and len(s['pods']) == 1 and all(c['ready'] for c in s['pods'][0]['containers'])

def patch(before, replacement):
    current, _, _ = deployment('api')
    assert current['uid'] == before['uid'] and current['specSha256'] == before['specSha256'], 'Independent spec change; stop'
    path = '/spec/template/spec/containers/'+str(before['index'])+'/image'
    ops = [{'op':'test','path':'/metadata/uid','value':before['uid']},
        {'op':'test','path':'/metadata/resourceVersion','value':current['resourceVersion']},
        {'op':'test','path':path,'value':before['image']}, {'op':'replace','path':path,'value':replacement}]
    args = ('patch','deployment','shopixy-api','-n','shopixy-app','--type=json','-p',json.dumps(ops))
    kube(*args,'--dry-run=server')
    kube(*args)
    kube('rollout','status','deployment/shopixy-api','-n','shopixy-app','--timeout=480s',timeout=510)

assert not (ROOT/'release-started.json').exists(), 'Existing attempt: inspect; do not retry blindly'
candidate = json.loads((ROOT/'candidate.json').read_text())
assert candidate['securityGatePassed'] is True and candidate['sourceInputsMatchStagedIndex'] is True
assert sha(Path(__file__).read_bytes()) == candidate['deploymentScriptSha256']
assert sha((ROOT/'api.tar.gz').read_bytes()) == candidate['archiveSha256']
assert len(json.loads(kube('get','nodes','-o','json'))['items']) == 1
before = {}
for kind in OLD:
    observed, spec, _ = deployment(kind)
    assert observed['image'] == OLD[kind] and observed['generation'] == GENERATIONS[kind] and ready(observed)
    before[kind] = observed
    if kind == 'api':
        expected_spec = copy.deepcopy(spec)
        expected_spec['template']['spec']['containers'][observed['index']]['image'] = candidate['image']
        expected_spec_sha = digest(expected_spec)
receipt = {'startedAtUtc':now(), 'before':before, 'candidate':candidate, 'businessDataWrites':False,
    'schemaChanges':False, 'configurationChanges':False, 'componentsChanged':['api']}
(ROOT/'release-started.json').write_text(json.dumps(receipt,indent=2)+'\n')
try:
    call('/usr/local/bin/k3s','ctr','images','import','--digests',str(ROOT/'api.tar.gz'),timeout=600)
    inspected = json.loads(call('/usr/local/bin/k3s','crictl','inspecti',candidate['image']))
    labels = inspected['info']['imageSpec']['config']['Labels']
    assert labels['shopixy.io/source-input-sha256'] == candidate['sourceInputSha256']
    assert inspected['status']['id'] == candidate['runtimeConfigId']
    patch(before['api'], candidate['image'])
    after = {kind:deployment(kind)[0] for kind in OLD}
    assert all(ready(item) for item in after.values())
    assert after['api']['specSha256'] == expected_spec_sha
    assert after['api']['generation'] == GENERATIONS['api'] + 1
    for kind in ('web','storefront'):
        assert after[kind]['specSha256'] == before[kind]['specSha256']
        assert after[kind]['generation'] == before[kind]['generation']
        assert after[kind]['pods'] == before[kind]['pods']
    _, spec, pods = deployment('api')
    pod = pods[0]
    for filename, expected_hash in candidate['assemblySha256'].items():
        assert filename in ('PointOfSale.API.dll','PointOfSale.Infrastructure.dll')
        actual = kube('exec','-n','shopixy-app',pod['metadata']['name'],'-c','api','--',
            '/usr/bin/sha256sum','/app/'+filename).split()[0]
        assert actual == expected_hash
    container = next(c for c in spec['template']['spec']['containers'] if c['name'] == 'api')
    probe = container['readinessProbe']['httpGet']
    port = probe['port']
    if isinstance(port,str): port = next(p['containerPort'] for p in container['ports'] if p['name'] == port)
    url = 'http://'+pod['status']['podIP']+':'+str(port)+probe['path']
    with urllib.request.urlopen(url, timeout=30) as response:
        assert response.status == 200
        health = {'path':probe['path'], 'status':response.status}
    receipt.update({'after':after,'readOnlyApplicationProbe':health,'liveAssemblyHashesMatchCandidate':True,'completedAtUtc':now()})
    (ROOT/'release-verified.json').write_text(json.dumps(receipt,indent=2)+'\n')
except Exception as error:
    current, _, _ = deployment('api')
    rolled_back = False
    if current['image'] == candidate['image'] and current['specSha256'] == expected_spec_sha:
        try:
            patch(current, OLD['api'])
            rolled_back = deployment('api')[0]['image'] == OLD['api'] and ready(deployment('api')[0])
        except Exception:
            pass
    receipt.update({'failedAtUtc':now(),'failureClass':type(error).__name__,'rollbackVerified':rolled_back})
    (ROOT/'release-failed.json').write_text(json.dumps(receipt,indent=2)+'\n')
    raise
print(json.dumps(receipt,indent=2))
