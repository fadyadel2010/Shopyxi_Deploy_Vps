"""Recover the exact CRI-normalized import reference identified by the retained pod event."""
from pathlib import Path
import hashlib
import json
import urllib.request

ROOT = Path('/var/lib/shopixy/affiliate-full-e2e-20261115')
candidate = json.loads((ROOT/'candidate.json').read_text())
original = (ROOT/'deploy-api-only.py').read_text()
assert hashlib.sha256(original.encode()).hexdigest() == candidate['deploymentScriptSha256']
exec(compile(original.split("assert not (ROOT/'release-started.json')",1)[0],str(ROOT/'deploy-api-only.py'),'exec'),globals())
assert not (ROOT/'normalized-alias-recovery-started.json').exists(), 'Inspect an existing attempt; do not repeat'
failed = json.loads((ROOT/'alias-recovery-failed.json').read_text())
assert failed['rollbackVerified'] is True and failed['candidate'] == candidate
before = {}
for kind in OLD:
    observed,spec,_ = deployment(kind)
    assert ready(observed) and observed['image'] == OLD[kind]
    assert observed['generation'] == (49 if kind == 'api' else GENERATIONS[kind])
    assert observed['specSha256'] == failed['before'][kind]['specSha256']
    assert observed['uid'] == failed['before'][kind]['uid']
    before[kind] = observed
    if kind == 'api':
        expected = copy.deepcopy(spec)
        expected['template']['spec']['containers'][observed['index']]['image'] = candidate['image']
        expected_spec_sha = digest(expected)
index_digest = candidate['image'].split('@')[1]
listing = call('/usr/local/bin/k3s','ctr','images','list').splitlines()
source = next(line.split()[0] for line in listing if line.split()[0].startswith('import-') and
    line.split()[0].endswith('@'+index_digest) and index_digest in line.split()[1:])
normalized = 'docker.io/library/'+source
assert all(line.split()[0] != normalized for line in listing)
index = json.loads(call('/usr/local/bin/k3s','ctr','content','get',index_digest))
selected = [json.loads(call('/usr/local/bin/k3s','ctr','content','get',item['digest'])) for item in index['manifests']]
assert sum(item.get('config',{}).get('digest') == candidate['runtimeConfigId'] for item in selected) == 1
config = json.loads(call('/usr/local/bin/k3s','ctr','content','get',candidate['runtimeConfigId']))
assert config['config']['Labels']['shopixy.io/source-input-sha256'] == candidate['sourceInputSha256']
assert config['config']['User'] == '1654:1654'
receipt = {'startedAtUtc':now(),'before':before,'candidate':candidate,
    'reason':'CRI failed image creation because the docker.io/library-normalized import alias was absent; no candidate API container started',
    'priorRollbackVerified':True,'normalizedImportReference':normalized,'businessDataWrites':False,
    'schemaChanges':False,'configurationChanges':False,'componentsChanged':['api'],'reimportedArchive':False}
(ROOT/'normalized-alias-recovery-started.json').write_text(json.dumps(receipt,indent=2)+'\n')
try:
    call('/usr/local/bin/k3s','ctr','images','tag',source,normalized)
    inspection = json.loads(call('/usr/local/bin/k3s','crictl','inspecti',candidate['runtimeConfigId']))
    assert inspection['status']['id'] == candidate['runtimeConfigId']
    assert normalized in inspection['status']['repoDigests']
    # Instantiate only the SHA-256 utility, not the API or a business workflow.
    smoke = call('/usr/local/bin/k3s','ctr','run','--rm','--net-host',candidate['image'],
        'affiliate-full-image-hash-probe','/usr/bin/sha256sum','/app/PointOfSale.API.dll',timeout=120)
    assert smoke.split()[0] == candidate['assemblySha256']['PointOfSale.API.dll']
    receipt['harmlessImageInstantiationPassed'] = True
    patch(before['api'],candidate['image'])
    after = {kind:deployment(kind)[0] for kind in OLD}
    assert all(ready(item) for item in after.values())
    assert after['api']['specSha256'] == expected_spec_sha and after['api']['generation'] == 50
    for kind in ('web','storefront'):
        assert after[kind]['specSha256'] == before[kind]['specSha256']
        assert after[kind]['generation'] == before[kind]['generation'] and after[kind]['pods'] == before[kind]['pods']
    _,spec,pods = deployment('api')
    pod = pods[0]
    for filename,expected_hash in candidate['assemblySha256'].items():
        assert filename in ('PointOfSale.API.dll','PointOfSale.Infrastructure.dll')
        assert kube('exec','-n','shopixy-app',pod['metadata']['name'],'-c','api','--',
            '/usr/bin/sha256sum','/app/'+filename).split()[0] == expected_hash
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
    (ROOT/'normalized-alias-recovery-failed.json').write_text(json.dumps(receipt,indent=2)+'\n')
    raise
print(json.dumps(receipt,indent=2))
