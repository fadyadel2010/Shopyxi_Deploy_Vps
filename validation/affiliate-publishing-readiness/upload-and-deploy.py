"""Deliver exact admitted archives and execute one guarded application-only release."""
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import sys

assert sys.argv[1:]==['--deploy-api-and-web'],'Explicit bounded deployment flag required'
root=Path(__file__).resolve().parents[2];out=Path(__file__).parent
data=json.loads((out/'release-input.json').read_text())
assert data['allGatesPassed'] and json.loads((out/'gate-summary.json').read_text())['passed']
assert json.loads((out/'production-boundary-before.json').read_text())['passed']
assert hashlib.sha256((out/'deploy-api-web.py').read_bytes()).hexdigest()==data['deploymentScriptSha256']
for kind,item in data['components'].items():
    path=root.parent/('publishing-readiness-production-'+kind)/(kind+'.tar.gz')
    assert hashlib.sha256(path.read_bytes()).hexdigest()==item['archiveSha256']
subprocess.run([sys.executable,str(out/'scan-reviewed.py')],check=True)
subprocess.run([sys.executable,str(out/'verify-runtime.py'),'pre-deploy'],check=True)
remote='/var/lib/shopixy/affiliate-publishing-readiness-20261115'
create='from pathlib import Path; Path('+repr(remote)+').mkdir(mode=0o700,exist_ok=False)'
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(create)],capture_output=True,check=True,timeout=60)
files=[(out/'release-input.json','release-input.json'),(out/'deploy-api-web.py','deploy-api-web.py')]
files += [(root.parent/('publishing-readiness-production-'+kind)/(kind+'.tar.gz'),kind+'.tar.gz') for kind in ('api','web')]
for path,name in files:
    program='import sys,shutil; f=open('+repr(remote+'/'+name)+',"xb"); shutil.copyfileobj(sys.stdin.buffer,f); f.close()'
    with path.open('rb') as stream:
        result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(program)],stdin=stream,capture_output=True,timeout=600)
    assert result.returncode==0,'Transfer failed; inspect artifact directory before any retry'
result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 '+remote+'/deploy-api-web.py'],capture_output=True,text=True,timeout=1800)
if result.returncode:
    (root.parent/'publishing-readiness-deployment-private.log').write_text(result.stdout+result.stderr)
    raise SystemExit('Release guard failed; inspect release-started/failed receipts before retrying. Raw output withheld.')
receipt=json.loads(result.stdout);assert receipt['completedAtUtc'] and receipt['componentsChanged']==['api','web']
(out/'production-deployment.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps({'completedAtUtc':receipt['completedAtUtc'],'images':{kind:receipt['after'][kind]['image'] for kind in ('api','web')},
    'generations':{kind:receipt['after'][kind]['generation'] for kind in ('api','web','storefront')},
    'deliveryMethod':receipt['deliveryMethod'],'businessDataWrites':False},indent=2))
