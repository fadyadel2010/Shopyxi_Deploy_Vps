"""Summarize actual persisted test counters and financial reconciliation, without credentials."""
import datetime
import json
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).parent
ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def counters(name):
    tree = ET.parse(root/'results'/name)
    return {key:int(value) for key,value in tree.find('t:ResultSummary/t:Counters',ns).attrib.items()}
before = counters('journey-before.trx')
focused = counters('journey-after.trx')
standard = counters('journey-standard-owned-fixture.trx')
assert before['total'] == before['failed'] == 1
assert focused['total'] == focused['passed'] == standard['total'] == standard['passed'] == 4
assert focused['failed'] == standard['failed'] == focused['notExecuted'] == standard['notExecuted'] == 0
financial = json.loads((root/'final-reconciliation-summary.json').read_text())
assert financial['result'] == 'PASS'
result = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'beforeFix':before, 'focusedAfterFix':focused, 'standardReleaseAssembly':standard,
    'financialReconciliation':financial, 'productionBusinessExecution':False,
    'physicalDeliveryOrRealMoneyClaimed':False, 'analyticsConversionBridgePassed':False}
(root/'gate-summary.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'beforeFixExpectedFailures':before['failed'],'focusedPassed':focused['passed'],
    'standardPassed':standard['passed'],'financialResult':financial['result']},indent=2))
