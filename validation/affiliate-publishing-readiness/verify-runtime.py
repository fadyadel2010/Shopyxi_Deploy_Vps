"""Fresh read-only Production identity verification; retain previous release evidence."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('previous_runtime', root/'validation/affiliate-full-e2e/verify-live-runtime.py')
previous = importlib.util.module_from_spec(spec)
spec.loader.exec_module(previous)
remote = previous.REMOTE.replace('880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3',
    '8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02')
stage = sys.argv[1] if len(sys.argv) == 2 else 'before'
assert stage in ('before','pre-deploy','after-rollback','after-release')
if stage == 'after-release':
    api = json.loads((Path(__file__).parent/'production-api-candidate.json').read_text())['image'].split('@sha256:')[1]
    web = json.loads((Path(__file__).parent/'production-web-candidate.json').read_text())['image'].split('@sha256:')[1]
    remote = remote.replace('8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02',api).replace('f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',web)
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'], input=remote,
    capture_output=True,text=True,timeout=180)
assert result.returncode == 0, 'Fresh runtime check failed; no deployment authorized'
data = json.loads(result.stdout)
(Path(__file__).parent/('production-runtime-'+stage+'.json')).write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
