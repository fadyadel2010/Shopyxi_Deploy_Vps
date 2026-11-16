"""Read-only live identity and protected-work verification; never overwrite evidence."""
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

out = Path(__file__).parent
root = out.parents[1]
original = Path('D:/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source')
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest() if p.is_file() else None
prior = json.loads((original/'validation/affiliate-production-acceptance/preservation-verification.json').read_text())
patch = digest(original/'docs/local-work/preserved-working-differences.patch')
assert patch == prior['preservedPatchSha256']
status = subprocess.check_output(['git','status','--porcelain'],cwd=original)
paths = [line[3:] for line in status.decode().splitlines()]
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'originalHead':subprocess.check_output(['git','rev-parse','HEAD'],cwd=original,text=True).strip(),
    'statusSha256':hashlib.sha256(status).hexdigest(), 'workingEntries':len(paths),
    'files':[{'path':p,'sha256':digest(original/p)} for p in paths],
    'preservedPatchSha256':patch,'preservedPatchUnchanged':True,
    'sourceBaseline':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()}
target = out/'protected-work-before.json'
assert not target.exists()
target.write_text(json.dumps(receipt,indent=2)+'\n')
spec = importlib.util.spec_from_file_location('runtime',root/'validation/affiliate-full-e2e/verify-live-runtime.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)
remote = runtime.REMOTE.replace('880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3',
    '39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b').replace(
    'f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',
    'ee6f3d73e076221dd600ea44c178afc8b811e2a32758eae9890553cd38486abd')
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],
    input=remote,text=True,capture_output=True,timeout=180)
assert result.returncode == 0, 'Fresh runtime verification failed; raw output withheld'
data = json.loads(result.stdout)
target = out/'production-runtime-before.json'
assert not target.exists()
target.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps({'preservedPatchUnchanged':True,'protectedWorkingEntries':len(paths),'production':data},indent=2))
