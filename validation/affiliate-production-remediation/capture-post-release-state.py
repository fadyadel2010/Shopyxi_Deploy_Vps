"""Read-only financial/QA snapshot; keep merchant amounts private and compare exact prior state."""
import datetime
import json
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
private = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-acceptance-private'
target = private / 'business-state-phase-two.json'
assert not target.exists(), 'Preserve previous capture; inspect before repeat'
sql = (root / 'validation/affiliate-production-acceptance/qa-state-readonly.sql').read_text()
p = subprocess.run(['ssh', 'shopixy-vps', 'sudo /usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
    input=sql, capture_output=True, text=True, timeout=90)
assert p.returncode == 0, 'Read-only capture failed; no raw data exported'
state = json.loads(p.stdout.strip()); target.write_text(json.dumps(state, indent=2))
before = json.loads((private / 'business-state-after.json').read_text())
safe = {k:v for k,v in state.items() if not k.endswith('Private')}
safe['unchangedSincePhaseOne'] = {k: before[k] == state[k] for k in state}
safe['verifiedAtUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
safe['privateAmountsExported'] = False
(root / 'validation/affiliate-production-remediation/business-state-post-release.json').write_text(json.dumps(safe, indent=2) + '\n')
print(json.dumps({'qaOrders':state['qaOrders'],'commissionEntries':state['commissionEntries'],'payoutRequests':state['payoutRequests'],
    'allComparedFieldsUnchanged':all(safe['unchangedSincePhaseOne'].values()),'privateAmountsExported':False}))
