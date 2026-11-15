"""Read the dedicated QA product's authoritative variant stock, never compatibility balances."""
import datetime
import json
from pathlib import Path
import subprocess
import sys

label=sys.argv[1]
assert label in ('before','after')
out=Path(__file__).resolve().parent
target=out/('inventory-'+label+'.json')
if target.exists(): raise SystemExit('Preserve existing snapshot')
p=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe','-o','BatchMode=yes','shopixy-vps',
    '/usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
    input=(out/'inventory-readonly.sql').read_text(),capture_output=True,text=True,timeout=90)
if p.returncode: raise SystemExit('Read-only inventory capture failed')
safe=json.loads(p.stdout.strip())
safe['verifiedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
if label=='after':
    before=json.loads((out/'inventory-before.json').read_text())
    safe['unchangedSinceBefore']=all(before[k]==v for k,v in safe.items() if k!='verifiedAtUtc')
target.write_text(json.dumps(safe,indent=2),encoding='utf-8')
print(json.dumps(safe,indent=2))
