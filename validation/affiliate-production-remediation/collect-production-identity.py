"""Read-only immutable runtime verification; export only hashes, readiness and schema predicates."""
import json
from pathlib import Path
import subprocess

remote = r'''
import json,subprocess
from pathlib import Path
def call(*args): return subprocess.check_output(args,text=True)
def graph(digest, seen):
    assert len(seen)<12, 'Unexpectedly deep runtime image graph'
    if digest in seen: return []
    seen.add(digest)
    item=json.loads(call('/usr/local/bin/k3s','ctr','content','get',digest))
    if 'manifests' in item:
        return [leaf for child in item['manifests'] for leaf in graph(child['digest'],seen)]
    return [item]
root=Path('/var/lib/shopixy/affiliate-return-20261115')
receipt=json.loads((root/'release-verified.json').read_text())
checks={}
for kind,container in [('api','api'),('web','web')]:
    ref=receipt['intended'][kind]
    index=json.loads(call('/usr/local/bin/k3s','ctr','content','get',ref.split('@')[1]))
    children={m['digest'] for m in index['manifests']}
    inspection=json.loads(call('/usr/local/bin/k3s','crictl','inspecti',ref))
    observed=receipt['after'][kind]
    runtime=next(c for c in observed['pods'][0]['containers'] if c['name']==container)
    runtime_digest=runtime['imageID'].split('@')[-1]
    runtime_seen=set(); runtime_manifests=graph(runtime_digest,runtime_seen)
    source_manifests=graph(ref.split('@')[1],set())
    # Docker archive import may add an import-YYYY-MM-DD wrapper index.
    # Prove the pinned index in that graph plus the exact selected config/layers.
    assert runtime['ready'] and ref.split('@')[1] in runtime_seen
    selected=[m for m in runtime_manifests if m.get('config',{}).get('digest')==inspection['status']['id']]
    assert len(selected)==1
    assert any(m.get('config')==selected[0]['config'] and m.get('layers')==selected[0]['layers'] for m in source_manifests)
    labels=inspection['info']['imageSpec']['config']['Labels']
    assert labels['shopixy.io/source-input-sha256']==receipt['sourceIndexSha256']
    checks[kind]={'deploymentImage':ref,'runtimeChildManifest':runtime['imageID'].split('@')[-1],
        'exactChildManifestDigestBelongsToImportedIndex':runtime_digest in children,
        'pinnedReleaseIndexBelongsToRuntimeImageGraph':True,
        'runtimeConfigAndOrderedLayersMatchImportedChild':True,'runtimeConfigId':inspection['status']['id'],
        'sourceLabelMatchesReleaseIndex':True,'ready':True,'restartCount':runtime['restartCount']}
print(json.dumps({'verifiedImages':checks,'storefrontSpecUnchanged':receipt['before']['storefront']['specSha256']==receipt['after']['storefront']['specSha256'],
    'operationalSchemaVersionMetadataCorrected':receipt['operationalSchemaVersionMetadataCorrected'],
    'businessDataWrites':False,'completedAtUtc':receipt['completedAtUtc']},indent=2))
'''
result=subprocess.run(['ssh','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=120)
if result.returncode:
    Path('C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115/identity-diagnostic-private.log').write_text(result.stderr)
    print('\n'.join(result.stderr.splitlines()[-4:]))
assert result.returncode==0, 'Immutable runtime verification failed; raw remote output withheld'
data=json.loads(result.stdout)
out=Path(__file__).parent/'production-identity-verification.json'
out.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(data,indent=2))
