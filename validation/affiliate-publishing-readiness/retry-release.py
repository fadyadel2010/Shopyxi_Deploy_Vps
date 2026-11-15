"""Upload a separately sealed retry, reusing verified remote archives; never overwrite attempt one."""
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import sys

assert sys.argv[1:]==['--deploy-api-and-web-retry1']
out=Path(__file__).parent
root=out.resolve().parents[1]
data=json.loads((out/'release-input.json').read_text())
original_hash=data['deploymentScriptSha256']
assert hashlib.sha256((out/'deploy-api-web.py').read_bytes()).hexdigest()==original_hash
assert data['allGatesPassed'] and json.loads((out/'production-boundary-after.json').read_text())['passed']
subprocess.run([sys.executable,str(out/'scan-reviewed.py')],check=True)
subprocess.run([sys.executable,str(out/'verify-runtime.py'),'after-rollback'],check=True)
data['originalDeploymentScriptSha256']=original_hash
data['deploymentScriptSha256']=hashlib.sha256((out/'deploy-api-web-retry.py').read_bytes()).hexdigest()
data['retryReason']='Preserve the exact outer archive index alias retained in the CRI config-ID cache; verify all repoDigests before deployment.'
(out/'release-retry-input.json').write_text(json.dumps(data,indent=2)+'\n')
original='/var/lib/shopixy/affiliate-publishing-readiness-20261115'
retry=original+'-retry1'
prepare='''
import hashlib,json,shutil
from pathlib import Path
old=Path(OLD);new=Path(NEW)
failed=json.loads((old/'release-failed.json').read_text())
assert failed['rollbackVerified']=={'api':True} and not (old/'release-verified.json').exists()
new.mkdir(mode=0o700,exist_ok=False)
for kind,item in INPUTS['components'].items():
 source=old/(kind+'.tar.gz');assert hashlib.sha256(source.read_bytes()).hexdigest()==item['archiveSha256']
 shutil.copyfile(source,new/source.name)
print(json.dumps({'failedAttempt':failed,'retryDirectoryCreated':True}))
'''.replace('OLD',repr(original)).replace('NEW',repr(retry)).replace('INPUTS',repr(data))
result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=prepare,text=True,capture_output=True,timeout=180)
assert result.returncode==0,'Retry preparation failed; inspect before any further action'
initial=json.loads(result.stdout)['failedAttempt']
(out/'production-deployment-failed.json').write_text(json.dumps(initial,indent=2)+'\n')
for path,name in ((out/'release-retry-input.json','release-input.json'),(out/'deploy-api-web-retry.py','deploy-api-web-retry.py')):
 program='import sys; f=open('+repr(retry+'/'+name)+',"xb"); f.write(sys.stdin.buffer.read()); f.close()'
 with path.open('rb') as stream:
  result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(program)],stdin=stream,capture_output=True,timeout=90)
 assert result.returncode==0,'Retry transfer failed; stop and inspect'
result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 '+retry+'/deploy-api-web-retry.py'],capture_output=True,text=True,timeout=1800)
if result.returncode:
 (root.parent/'publishing-readiness-retry-deployment-private.log').write_text(result.stdout+result.stderr)
 raise SystemExit('Bounded retry failed; no additional retry authorized by this helper. Inspect retained receipts.')
receipt=json.loads(result.stdout)
assert receipt['completedAtUtc'] and receipt['componentsChanged']==['api','web']
(out/'production-deployment.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps({'completedAtUtc':receipt['completedAtUtc'],'generations':{k:receipt['after'][k]['generation'] for k in ('api','web','storefront')},'runtimeContentsMatchCandidates':receipt['runtimeContentsMatchCandidates'],'businessDataWrites':False},indent=2))
