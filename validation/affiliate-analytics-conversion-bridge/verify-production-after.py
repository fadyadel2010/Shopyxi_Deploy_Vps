"""Independent read-only deployment/content verification, not a release-receipt assertion."""
import importlib.util,json,subprocess
from pathlib import Path
out=Path(__file__).parent;root=out.parents[1]
spec=importlib.util.spec_from_file_location('runtime',root/'validation/affiliate-full-e2e/verify-live-runtime.py');runtime=importlib.util.module_from_spec(spec);spec.loader.exec_module(runtime)
api=json.loads(max(out.glob('production-api-candidate*.json'),key=lambda p:p.stat().st_mtime).read_text())
web=json.loads(max(out.glob('production-web-candidate*.json'),key=lambda p:p.stat().st_mtime).read_text())
remote=runtime.REMOTE.replace('880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3',api['image'].split('@sha256:')[1]).replace('f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991',web['image'].split('@sha256:')[1])
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=240)
if r.returncode: raise SystemExit('Independent runtime verification failed; raw diagnostic withheld')
data=json.loads(r.stdout);target=out/('production-runtime-final.json' if (out/'production-runtime-after.json').exists() else 'production-runtime-after.json');assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
