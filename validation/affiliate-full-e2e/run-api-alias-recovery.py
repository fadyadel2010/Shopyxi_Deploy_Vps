"""Capture the stopped attempt and execute only the reviewed import-alias recovery."""
import json
from pathlib import Path
import shlex
import subprocess
import sys

assert sys.argv[1:] == ['--recover-verified-import-alias']
out = Path(__file__).parent
remote_root = '/var/lib/shopixy/affiliate-full-e2e-20261115'
subprocess.run([sys.executable,str(out/'scan-reviewed.py')],check=True)
failed = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo cat '+remote_root+'/release-failed.json'],
    capture_output=True,text=True,check=True,timeout=90)
receipt = json.loads(failed.stdout)
assert receipt['before']['api']['generation'] == 47
target = out/'production-release-attempts.json'
assert not target.exists(), 'Retain previous attempt evidence; inspect before repeating recovery'
target.write_text(json.dumps({'initialAttempt':receipt},indent=2)+'\n',encoding='utf-8',newline='\n')
program = 'import sys; f=open('+repr(remote_root+'/recover-api-import-alias.py')+',"xb"); f.write(sys.stdin.buffer.read()); f.close()'
uploaded = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(program)],
    input=(out/'recover-api-import-alias.py').read_bytes(),capture_output=True,timeout=90)
assert uploaded.returncode == 0, 'Recovery script already exists or upload failed; do not overwrite'
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 '+remote_root+'/recover-api-import-alias.py'],
    capture_output=True,text=True,timeout=900)
if result.returncode:
    (out.resolve().parents[1].parent/'production-api-alias-recovery-private.log').write_text(result.stderr)
    raise SystemExit('Alias recovery stopped; inspect current deployment and recovery receipt, not a blind retry')
verified = json.loads(result.stdout)
(out/'production-deployment.json').write_text(json.dumps(verified,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'completedAtUtc':verified['completedAtUtc'],'apiImage':verified['after']['api']['image'],
    'apiGeneration':verified['after']['api']['generation'],'webAndStorefrontUnchanged':True,
    'readOnlyApplicationProbe':verified['readOnlyApplicationProbe'],'liveAssemblyHashesMatchCandidate':True},indent=2))
