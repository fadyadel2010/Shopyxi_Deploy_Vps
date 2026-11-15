"""Transfer the admitted artifact and run the one-shot API-only Production guard."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import shlex

assert sys.argv[1:] == ['--deploy-api-only'], 'Explicit bounded deployment flag required'
root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
candidate_root = root.parent/'production-api-candidate'
candidate = json.loads((candidate_root/'candidate.json').read_text())
sha = lambda value:hashlib.sha256(value).hexdigest()
assert candidate == json.loads((out/'production-api-candidate.json').read_text())
assert sha((out/'deploy-api-only.py').read_bytes()) == candidate['deploymentScriptSha256']
assert sha((candidate_root/'api.tar.gz').read_bytes()) == candidate['archiveSha256']
assert json.loads((out/'production-boundary-before-api-release.json').read_text())['passed']
assert json.loads((out/'gate-summary.json').read_text())['standardReleaseAssembly']['passed'] == 4
assert json.loads((out/'production-security-gate.json').read_text())['passed']
subprocess.run([sys.executable,str(out/'scan-reviewed.py')],cwd=root,check=True)
subprocess.run([sys.executable,str(out/'verify-live-runtime.py')],cwd=root,check=True)
remote_root = '/var/lib/shopixy/affiliate-full-e2e-20261115'
create = "from pathlib import Path; p=Path("+repr(remote_root)+"); p.mkdir(mode=0o700,exist_ok=False)"
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(create)],check=True,capture_output=True,timeout=60)
for path, name in ((candidate_root/'api.tar.gz','api.tar.gz'), (candidate_root/'candidate.json','candidate.json'),
    (out/'deploy-api-only.py','deploy-api-only.py')):
    program = 'import sys,shutil; f=open('+repr(remote_root+'/'+name)+',"xb"); shutil.copyfileobj(sys.stdin.buffer,f); f.close()'
    with path.open('rb') as stream:
        uploaded = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(program)],
            stdin=stream,capture_output=True,timeout=600)
    assert uploaded.returncode == 0, 'Upload failed; inspect fixed artifact directory before any retry'
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 '+remote_root+'/deploy-api-only.py'],
    capture_output=True,text=True,timeout=1200)
if result.returncode:
    (root.parent/'production-api-deploy-private.log').write_text(result.stderr)
    raise SystemExit('Release guard failed; inspect remote release-started/failed receipts before any retry. Raw log retained privately.')
receipt = json.loads(result.stdout)
assert receipt['completedAtUtc'] and receipt['componentsChanged'] == ['api']
(out/'production-deployment.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'completedAtUtc':receipt['completedAtUtc'],'apiImage':receipt['after']['api']['image'],
    'apiGeneration':receipt['after']['api']['generation'],'readOnlyApplicationProbe':receipt['readOnlyApplicationProbe'],
    'webAndStorefrontUnchanged':True,'businessDataWrites':False},indent=2))
