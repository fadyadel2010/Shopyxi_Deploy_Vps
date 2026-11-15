"""Exact before/after reads. Non-QA merchant amounts remain in the approved private temp directory."""
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys

label=sys.argv[1]
assert label in ('before','after')
out=Path(__file__).resolve().parent
private=Path(os.environ['LOCALAPPDATA'])/'Temp/opencode/shopixy-affiliate-acceptance-private'
target=private/('business-state-'+label+'.json')
if target.exists(): raise SystemExit('Snapshot already captured; preserve it')
sql=(out/'qa-state-readonly.sql').read_text(encoding='utf-8')
p=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe','-o','BatchMode=yes','shopixy-vps',
    '/usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
    input=sql,capture_output=True,text=True,timeout=90)
if p.returncode:
    # SQL contains only fixed QA IDs; export first error message, not statements/records.
    errors=[l for l in p.stderr.splitlines() if 'ERROR:' in l]
    print('\n'.join(errors[:1]));raise SystemExit('Read-only state capture failed')
state=json.loads(p.stdout.strip())
target.write_text(json.dumps(state,indent=2),encoding='utf-8')
safe={k:v for k,v in state.items() if not k.endswith('Private')}
safe['verifiedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
safe['scope']='No QA order, stock receipt, payment or payout submitted. This snapshot does not prove a transaction/reversal workflow.'
if label=='after':
    before=json.loads((private/'business-state-before.json').read_text())
    safe['unchangedSinceBefore']={k:before[k]==state[k] for k in state}
    safe['privateAmountsExported']=False
(out/('business-state-'+label+'.json')).write_text(json.dumps(safe,indent=2),encoding='utf-8')
print(json.dumps(safe,indent=2))
