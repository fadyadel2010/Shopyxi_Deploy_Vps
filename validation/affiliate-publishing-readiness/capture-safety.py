"""Read-only fingerprints of existing isolated financial history; never export row bodies or secrets."""
import datetime
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
stage = sys.argv[1]
assert stage in ('before', 'after')
spec = importlib.util.spec_from_file_location('local_stack', root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec); spec.loader.exec_module(stack)
private = root.parent/'publishing-readiness-history-review-private.json'
tables = ['Orders','OrderItems','OrderRefunds','OrderRefundLines','AffiliateAttributions',
    'AffiliateCommissionLedger','AffiliateBalanceLedger','AffiliatePayoutRequests','AffiliateManualPayouts',
    'AffiliateManualPayoutReturns','AffiliateManualPayoutReturnAllocations','PaymentSettlementBatches',
    'PaymentSettlementOrders','LedgerJournalEntries','LedgerLines','StockMovements','InventoryReservations']
prior = json.loads(private.read_text()) if stage == 'after' else None
assert stage == 'after' or not private.exists(), 'Preserve original historical fingerprints'
captured = {}
for table in tables:
    restriction = '' if prior is None else ' WHERE "Id" IN (' + ','.join("'"+row['id']+"'" for row in prior[table]) + ')'
    if prior is not None and not prior[table]: restriction = ' WHERE false'
    sql = 'BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT coalesce(json_agg(x ORDER BY x.id),\'[]\'::json) FROM (SELECT "Id"::text id, md5(row_to_json(t)::text) fingerprint FROM "'+table+'" t'+restriction+') x; ROLLBACK;'
    result = subprocess.run([*stack.DOCKER,'exec','-i','affiliate-full-postgres','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','affiliate_fixture','-d','affiliate_full_e2e'],
        input=sql,text=True,capture_output=True,timeout=60)
    if result.returncode:
        (root.parent/'publishing-readiness-safety-private.log').write_text(result.stderr)
        raise RuntimeError('Read-only history fingerprint query failed: '+table)
    captured[table] = json.loads(result.stdout.strip())
if stage == 'before': private.write_text(json.dumps(captured))
else: assert captured == prior, 'Existing historical row changed or disappeared'
receipt = {'capturedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stage':stage,
    'readOnlyRepeatableRead':True,'existingRowCounts':{table:len(rows) for table,rows in captured.items()},
    'allExistingHistoricalRowsUnchanged':True if stage == 'after' else None, 'privateRowBodiesExported':False}
(out/('history-safety-review-'+stage+'.json')).write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(receipt,indent=2))
