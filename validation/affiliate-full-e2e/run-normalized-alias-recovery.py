"""Retain the rollback receipt and run the specific, diagnosed normalized-alias correction once."""
import json
from pathlib import Path
import shlex
import subprocess
import sys

assert sys.argv[1:] == ['--recover-diagnosed-normalized-alias']
out = Path(__file__).parent
remote_root = '/var/lib/shopixy/affiliate-full-e2e-20261115'
subprocess.run([sys.executable,str(out/'scan-reviewed.py')],check=True)
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo cat '+remote_root+'/alias-recovery-failed.json'],
    capture_output=True,text=True,check=True,timeout=90)
failed = json.loads(result.stdout)
assert failed['rollbackVerified'] is True
target = out/'production-alias-rollback.json'
assert not target.exists()
target.write_text(json.dumps(failed,indent=2)+'\n',encoding='utf-8',newline='\n')
name = 'recover-normalized-import-alias.py'
program = 'import sys; f=open('+repr(remote_root+'/'+name)+',"xb"); f.write(sys.stdin.buffer.read()); f.close()'
uploaded = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -c '+shlex.quote(program)],
    input=(out/name).read_bytes(),capture_output=True,timeout=90)
assert uploaded.returncode == 0, 'Do not overwrite an existing recovery'
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 '+remote_root+'/'+name],
    capture_output=True,text=True,timeout=900)
if result.returncode:
    (out.resolve().parents[1].parent/'production-normalized-alias-recovery-private.log').write_text(result.stderr)
    raise SystemExit('Diagnosed alias recovery stopped; inspect receipt and current state before further action')
receipt = json.loads(result.stdout)
(out/'production-deployment.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'completedAtUtc':receipt['completedAtUtc'],'apiImage':receipt['after']['api']['image'],
    'apiGeneration':receipt['after']['api']['generation'],'webAndStorefrontUnchanged':True,
    'readOnlyApplicationProbe':receipt['readOnlyApplicationProbe'],'liveAssemblyHashesMatchCandidate':True},indent=2))
