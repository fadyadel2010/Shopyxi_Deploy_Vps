"""One-shot application-image-only VPS release. No schema, configuration or business writes."""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

ROOT = Path('/var/lib/shopixy/affiliate-publishing-readiness-20261115')
OLD = {'api':'docker.io/shopixy/api@sha256:8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02',
    'web':'docker.io/shopixy/web@sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',
    'storefront':'docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df'}
GENERATIONS = {'api':50,'web':15,'storefront':10}
def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat()
def sha(value): return hashlib.sha256(value).hexdigest()
def seal(value): return sha(json.dumps(value,sort_keys=True,separators=(',',':')).encode())
def call(*args,timeout=180):
    result = subprocess.run(args,capture_output=True,text=True,timeout=timeout)
    if result.returncode:
        (ROOT/'last-command-error-private.log').write_text(result.stderr)
        raise RuntimeError('Application release command failed; raw output withheld')
    return result.stdout
def kube(*args,timeout=180): return call('/usr/local/bin/kubectl','--request-timeout=30s',*args,timeout=timeout)
def deployment(kind):
    d = json.loads(kube('get','deployment','shopixy-'+kind,'-n','shopixy-app','-o','json'))
    spec = d['spec']; index = next(i for i,c in enumerate(spec['template']['spec']['containers']) if c['name']==kind)
    selector = ','.join(k+'='+v for k,v in spec['selector']['matchLabels'].items())
    pods = [p for p in json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items'] if not p['metadata'].get('deletionTimestamp')]
    status = d.get('status',{})
    safe = {'uid':d['metadata']['uid'],'resourceVersion':d['metadata']['resourceVersion'],'generation':d['metadata']['generation'],
        'observedGeneration':status.get('observedGeneration'),'image':spec['template']['spec']['containers'][index]['image'],
        'specSha256':seal(spec),'index':index,'replicas':spec.get('replicas'),
        **{key:status.get(key,0) for key in ('readyReplicas','availableReplicas','updatedReplicas')},
        'pods':[{'name':p['metadata']['name'],'uid':p['metadata']['uid'],'containers':[{key:c.get(key) for key in ('name','imageID','ready','restartCount')} for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}
    return safe,spec,pods
def ready(item):
    return item['replicas']==item['readyReplicas']==item['availableReplicas']==item['updatedReplicas']==1 and item['generation']==item['observedGeneration'] and len(item['pods'])==1 and all(c['ready'] for c in item['pods'][0]['containers'])
def patch(kind,before,replacement):
    current,_,_ = deployment(kind)
    assert current['uid']==before['uid'] and current['specSha256']==before['specSha256'],'Independent deployment change; stop'
    path = '/spec/template/spec/containers/'+str(current['index'])+'/image'
    operations = [{'op':'test','path':'/metadata/uid','value':current['uid']},
        {'op':'test','path':'/metadata/resourceVersion','value':current['resourceVersion']},
        {'op':'test','path':path,'value':current['image']},{'op':'replace','path':path,'value':replacement}]
    args = ('patch','deployment','shopixy-'+kind,'-n','shopixy-app','--type=json','-p',json.dumps(operations))
    kube(*args,'--dry-run=server');kube(*args)
    kube('rollout','status','deployment/shopixy-'+kind,'-n','shopixy-app','--timeout=480s',timeout=510)
def content(digest): return json.loads(call('/usr/local/bin/k3s','ctr','content','get',digest))
def manifests(digest):
    node = content(digest)
    return [leaf for child in node['manifests'] for leaf in manifests(child['digest'])] if 'manifests' in node else [node]
def image_admission(kind,item):
    archive = ROOT/(kind+'.tar.gz')
    assert sha(archive.read_bytes())==item['archiveSha256']
    call('/usr/local/bin/k3s','ctr','images','import','--digests',str(archive),timeout=600)
    digest = item['image'].split('@')[1]
    selected = [m for m in manifests(digest) if m.get('config',{}).get('digest')==item['runtimeConfigId']]
    assert len(selected)==1 and selected[0]['layers']
    config = content(item['runtimeConfigId'])['config']
    assert config['Labels']['shopixy.io/source-input-sha256']==item['sourceInputSha256']
    assert config['User'] not in ('','0','root','0:0')
    listing = call('/usr/local/bin/k3s','ctr','images','list').splitlines()[1:]
    references = {line.split()[0]:line.split() for line in listing if line.split()}
    source = next(ref for ref,row in references.items() if ref.startswith('import-') and ref.endswith('@'+digest) and digest in row[1:])
    # Admit both names diagnosed by the previous release BEFORE changing a Pod.
    for alias in (item['image'],'docker.io/library/'+source):
        if alias in references: assert digest in references[alias][1:]
        else: call('/usr/local/bin/k3s','ctr','images','tag',source,alias)
    inspection = json.loads(call('/usr/local/bin/k3s','crictl','inspecti',item['image']))
    assert inspection['status']['id']==item['runtimeConfigId']
    probe = item['probe']
    actual = call('/usr/local/bin/k3s','ctr','run','--rm','--net-host',item['image'],'affiliate-readiness-'+kind+'-hash-probe','/usr/bin/sha256sum',probe['path'],timeout=120)
    assert actual.split()[0]==probe['sha256']
    return {'importedIndexAndConfigVerified':True,'normalizedAliasAdmitted':True,'harmlessImageInstantiationPassed':True}

assert not (ROOT/'release-started.json').exists(),'Inspect an existing attempt; never repeat blindly'
inputs = json.loads((ROOT/'release-input.json').read_text())
assert sha(Path(__file__).read_bytes())==inputs['deploymentScriptSha256']
assert inputs['allGatesPassed'] and all(item['securityGatePassed'] for item in inputs['components'].values())
before = {};expected = {};admission = {}
for kind in OLD:
    item,spec,_ = deployment(kind)
    assert item['image']==OLD[kind] and item['generation']==GENERATIONS[kind] and ready(item)
    before[kind]=item
    if kind in inputs['components']:
        proposed=copy.deepcopy(spec);proposed['template']['spec']['containers'][item['index']]['image']=inputs['components'][kind]['image']
        expected[kind]=seal(proposed)
receipt = {'startedAtUtc':now(),'before':before,'componentsChanged':['api','web'],'businessDataWrites':False,
    'schemaChanges':False,'configurationChanges':False,'infrastructureChanges':False,'deliveryMethod':'verified archive import into existing k3s node; no registry push'}
(ROOT/'release-started.json').write_text(json.dumps(receipt,indent=2)+'\n')
try:
    for kind,item in inputs['components'].items(): admission[kind]=image_admission(kind,item)
    for kind in ('api','web'):
        patch(kind,before[kind],inputs['components'][kind]['image'])
        observed,_,_ = deployment(kind)
        assert ready(observed) and observed['specSha256']==expected[kind] and observed['generation']==GENERATIONS[kind]+1
    after = {kind:deployment(kind)[0] for kind in OLD}
    assert all(ready(item) for item in after.values())
    assert after['storefront']['specSha256']==before['storefront']['specSha256'] and after['storefront']['generation']==10 and after['storefront']['pods']==before['storefront']['pods']
    for kind,item in inputs['components'].items():
        _,_,pods = deployment(kind)
        for artifact in item['runtimeHashes']:
            actual = kube('exec','-n','shopixy-app',pods[0]['metadata']['name'],'-c',kind,'--','/usr/bin/sha256sum',artifact['path']).split()[0]
            assert actual==artifact['sha256']
    _,spec,pods=deployment('api');container=next(c for c in spec['template']['spec']['containers'] if c['name']=='api')
    probe=container['readinessProbe']['httpGet'];port=probe['port']
    if isinstance(port,str):port=next(p['containerPort'] for p in container['ports'] if p['name']==port)
    with urllib.request.urlopen('http://'+pods[0]['status']['podIP']+':'+str(port)+probe['path'],timeout=30) as response:assert response.status==200
    receipt.update({'after':after,'imageAdmission':admission,'runtimeContentsMatchCandidates':True,'apiReadinessHttpStatus':200,'completedAtUtc':now()})
    (ROOT/'release-verified.json').write_text(json.dumps(receipt,indent=2)+'\n')
except Exception as error:
    rollbacks={}
    for kind in ('web','api'):
        current,_,_=deployment(kind)
        if current['image']==inputs['components'][kind]['image'] and current['specSha256']==expected[kind]:
            try:patch(kind,current,OLD[kind]);rollbacks[kind]=ready(deployment(kind)[0])
            except Exception:rollbacks[kind]=False
    receipt.update({'failedAtUtc':now(),'failureClass':type(error).__name__,'rollbackVerified':rollbacks,'imageAdmission':admission})
    (ROOT/'release-failed.json').write_text(json.dumps(receipt,indent=2)+'\n')
    raise
print(json.dumps(receipt,indent=2))
