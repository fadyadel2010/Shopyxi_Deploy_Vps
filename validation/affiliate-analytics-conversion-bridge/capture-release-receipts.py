"""Retain sanitized remote release evidence without overwriting earlier attempts."""
import json,subprocess,sys
from pathlib import Path
out=Path(__file__).parent;directory=sys.argv[1]
assert directory.startswith('/var/lib/shopixy/affiliate-analytics-20261116')
remote="""from pathlib import Path
import json
p=Path(%r)
print(json.dumps({x.name:json.loads(x.read_text()) for x in p.glob('release-*.json') if x.name!='release-inputs.json'}))
"""%directory
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=180);assert r.returncode==0
data=json.loads(r.stdout);target=out/(directory.split('/')[-1]+'-receipts.json');assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps({'receiptFiles':list(data),'verifiedReleaseRetained':'release-verified.json' in data},indent=2))
