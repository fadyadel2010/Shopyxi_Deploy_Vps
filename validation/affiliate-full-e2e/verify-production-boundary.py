"""Prove this isolated fixture's identities/order/money were never created in Production."""
import datetime
import json
from pathlib import Path
import subprocess
import sys

stage = sys.argv[1]
assert stage in ('before-api-release', 'after-api-release')
sql = '''BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='20s';
SELECT json_build_object(
 'isolatedTenantRows',(SELECT count(*) FROM "Tenants" WHERE "Id"='15150000-2026-4015-a000-000000000001'),
 'isolatedOwnerRows',(SELECT count(*) FROM "Users" WHERE "Id"='15150000-2026-4015-a000-000000000002'),
 'isolatedAffiliateRows',(SELECT count(*) FROM "AffiliateAccounts" WHERE "Id"='01a1df98-5257-7282-9e19-f9f8ae5b6b74'),
 'isolatedProductRows',(SELECT count(*) FROM "Products" WHERE "Id"='37d9a1b6-6585-f655-0e55-824a517ce90c'),
 'isolatedOrderRows',(SELECT count(*) FROM "Orders" WHERE "Id"='8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12'),
 'isolatedJournalRows',(SELECT count(*) FROM "LedgerJournalEntries" WHERE "TenantId"='15150000-2026-4015-a000-000000000001'),
 'isolatedPayoutRows',(SELECT count(*) FROM "AffiliateManualPayouts" WHERE "Id"='9ed77774-b08d-4507-bdfa-4e8892eb52ea'),
 'isolatedPayoutReturnRows',(SELECT count(*) FROM "AffiliateManualPayoutReturns" WHERE "Id"='ba48a6a9-2f03-4ea3-a90a-deb6466a60e7'));
ROLLBACK;'''
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps',
    'sudo /usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
    input=sql, text=True, capture_output=True, timeout=90)
assert result.returncode == 0, 'Read-only boundary check failed; raw output withheld'
counts = json.loads(result.stdout.strip())
assert all(value == 0 for value in counts.values()), 'Isolated fixture leaked across Production boundary'
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(), 'stage':stage,
    'readOnlyRepeatableRead':True, 'businessDataWrites':False, 'fixtureCounts':counts, 'passed':True}
path = Path(__file__).parent / ('production-boundary-'+stage+'.json')
assert not path.exists(), 'Preserve prior boundary receipts'
path.write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
