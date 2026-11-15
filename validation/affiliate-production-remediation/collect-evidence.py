"""Export only sanitized gate counts, case names and source hashes; never raw environment/logs."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
base = Path('C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115')
evidence = root / 'validation/affiliate-production-remediation'
trx = base / 'source/validation/affiliate-production-remediation/results/affiliate-remediation.trx'
tree = ET.parse(trx)
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
counts = tree.find('t:ResultSummary/t:Counters', ns)
assert counts is not None
attributes = {k: int(v) for k, v in counts.attrib.items()}
assert attributes['failed'] == 0 and attributes['passed'] == attributes['total']
cases = tree.findall('.//t:UnitTestResult', ns)
assert all(case.attrib['outcome'] == 'Passed' for case in cases)
log = (base / 'isolated-tests.log').read_text()
assert re.search(r'Passed!.*Failed:\s+0', log)
assert 'PASS: additive migration, append-only guards, narrow runtime grants and retention-preserving grant rollback rehearsed' in log
frontend = json.loads((base / 'frontend-gates.json').read_text())
assert frontend['tests'] and frontend['typescript'] and frontend['build']
summary = {
    'capturedAtUtc': datetime.now(timezone.utc).isoformat(),
    'baselineCommit': 'bd25235',
    'productionDeployment': False,
    'isolatedFinancialBoundary': 'Approved commission -> native withdrawal/approval/manual cash payout -> full return/replay -> new withdrawal/re-payment',
    'notFullCheckoutFulfillmentE2E': True,
    'externalNetwork': 'None: network-none namespace, loopback-only MongoDB and local PostgreSQL Unix socket',
    'realPaymentsCarriersOrCustomerMessages': False,
    'dotnetCounters': attributes,
    'testCases': sorted(case.attrib['testName'] for case in cases),
    'schemaSnapshotParity': True,
    'migrationAndImmutableGuardsRehearsed': True,
    'runtimeGrantsAndRetainingRevokeRehearsed': True,
    'privateTrxSha256': hashlib.sha256(trx.read_bytes()).hexdigest(),
    'frontend': frontend,
}
(evidence / 'gate-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print('Sanitized evidence written:', attributes['passed'], '.NET tests and', frontend['counts']['passed'], 'frontend tests; no Production deployment.')
