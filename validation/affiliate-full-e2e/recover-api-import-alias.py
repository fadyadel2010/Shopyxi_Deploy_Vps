"""Explicit one-shot recovery of a verified import alias; never reimport or rerun a failed release blindly."""
from pathlib import Path
import json
import hashlib
import urllib.request

ROOT = Path('/var/lib/shopixy/affiliate-full-e2e-20261115')
candidate = json.loads((ROOT/'candidate.json').read_text())
original = (ROOT/'deploy-api-only.py').read_text()
assert hashlib.sha256(original.encode()).hexdigest() == candidate['deploymentScriptSha256']
# Load only the already reviewed imports/constants/helper definitions; no original main executes.
helpers = original.split("assert not (ROOT/'release-started.json')",1)[0]
exec(compile(helpers,str(ROOT/'deploy-api-only.py'),'exec'),globals())
assert not (ROOT/'alias-recovery-started.json').exists(), 'Existing recovery: inspect instead of repeating'
failed = json.loads((ROOT/'release-failed.json').read_text())
assert failed['candidate'] == candidate and failed['failureClass'] == 'RuntimeError'
before = {}
for kind in OLD:
    observed, spec, _ = deployment(kind)
    assert ready(observed) and observed['image'] == OLD[kind] and observed['generation'] == GENERATIONS[kind]
    assert observed['uid'] == failed['before'][kind]['uid']
    assert observed['specSha256'] == failed['before'][kind]['specSha256']
    before[kind] = observed
    if kind == 'api':
        expected_spec = copy.deepcopy(spec)
        expected_spec['template']['spec']['containers'][observed['index']]['image'] = candidate['image']
        expected_spec_sha = digest(expected_spec)
index_digest = candidate['image'].split('@')[1]
index = json.loads(call('/usr/local/bin/k3s','ctr','content','get',index_digest))
selected = []
for descriptor in index['manifests']:
    manifest = json.loads(call('/usr/local/bin/k3s','ctr','content','get',descriptor['digest']))
    if manifest.get('config',{}).get('digest') == candidate['runtimeConfigId']:
        selected.append(manifest)
assert len(selected) == 1 and selected[0]['layers']
config = json.loads(call('/usr/local/bin/k3s','ctr','content','get',candidate['runtimeConfigId']))
assert config['config']['Labels']['shopixy.io/source-input-sha256'] == candidate['sourceInputSha256']
assert config['config']['User'] == '1654:1654'
listing = call('/usr/local/bin/k3s','ctr','images','list').splitlines()
source = next(line.split()[0] for line in listing if line.split()[0].startswith('import-') and
    line.split()[0].endswith('@'+index_digest) and index_digest in line.split()[1:])
receipt = {'startedAtUtc':now(),'before':before,'candidate':candidate,
    'reason':'Canonical image alias absent after digest-only archive import; original attempt never patched a deployment',
    'importedIndexConfigAndSourceVerified':True, 'businessDataWrites':False,'schemaChanges':False,
    'configurationChanges':False,'componentsChanged':['api'],'reimportedArchive':False}
(ROOT/'alias-recovery-started.json').write_text(json.dumps(receipt,indent=2)+'\n')
try:
    call('/usr/local/bin/k3s','ctr','images','tag',source,candidate['image'])
    inspected = json.loads(call('/usr/local/bin/k3s','crictl','inspecti',candidate['image']))
    assert inspected['status']['id'] == candidate['runtimeConfigId']
    assert inspected['info']['imageSpec']['config']['Labels']['shopixy.io/source-input-sha256'] == candidate['sourceInputSha256']
    patch(before['api'],candidate['image'])
    after = {kind:deployment(kind)[0] for kind in OLD}
    assert all(ready(item) for item in after.values())
    assert after['api']['specSha256'] == expected_spec_sha and after['api']['generation'] == 48
    for kind in ('web','storefront'):
        assert after[kind]['specSha256'] == before[kind]['specSha256']
        assert after[kind]['generation'] == before[kind]['generation'] and after[kind]['pods'] == before[kind]['pods']
    _, spec, pods = deployment('api')
    pod = pods[0]
    for filename,expected in candidate['assemblySha256'].items():
        assert filename in ('PointOfSale.API.dll','PointOfSale.Infrastructure.dll')
        actual = kube('exec','-n','shopixy-app',pod['metadata']['name'],'-c','api','--','/usr/bin/sha256sum','/app/'+filename).split()[0]
        assert actual == expected
    container = next(c for c in spec['template']['spec']['containers'] if c['name'] == 'api')
    probe = container['readinessProbe']['httpGet']
    port = probe['port']
    if isinstance(port,str): port = next(p['containerPort'] for p in container['ports'] if p['name'] == port)
    with urllib.request.urlopen('http://'+pod['status']['podIP']+':'+str(port)+probe['path'],timeout=30) as response:
        assert response.status == 200
        health = {'path':probe['path'],'status':response.status}
    receipt.update({'after':after,'readOnlyApplicationProbe':health,'liveAssemblyHashesMatchCandidate':True,'completedAtUtc':now()})
    (ROOT/'release-verified.json').write_text(json.dumps(receipt,indent=2)+'\n')
except Exception as error:
    current,_,_ = deployment('api')
    rolled_back = False
    if current['image'] == candidate['image'] and current['specSha256'] == expected_spec_sha:
        try:
            patch(current,OLD['api'])
            rolled_back = deployment('api')[0]['image'] == OLD['api'] and ready(deployment('api')[0])
        except Exception:
            pass
    receipt.update({'failedAtUtc':now(),'failureClass':type(error).__name__,'rollbackVerified':rolled_back})
    (ROOT/'alias-recovery-failed.json').write_text(json.dumps(receipt,indent=2)+'\n')
    raise
print(json.dumps(receipt,indent=2))
