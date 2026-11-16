"""Read-only isolated outbox and retry evidence; no payload/customer/connection export."""
import datetime,importlib.util,json
from pathlib import Path
out=Path(__file__).parent
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
sql='''BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_agg(json_build_object('eventId',"Id",'tenantId',"TenantId",'orderId',"AggregateId",
 'eventVersion',"EventVersion",'createdAtUtc',"CreatedAtUtc",'processedAtUtc',"ProcessedAtUtc",
 'retryCount',"RetryCount",'requiresOperatorReconciliation',"RequiresOperatorReconciliation"))
FROM "OutboxMessages" WHERE "Id"='00f84cd5-2a55-4172-8f3b-a74de771f01e'
AND "EventType" LIKE '%AffiliateOrderPlacedEvent%'; ROLLBACK;'''
rows=json.loads(local.query(sql));assert rows and len(rows)==1 and rows[0]['processedAtUtc'] and not rows[0]['requiresOperatorReconciliation']
retries=[]
containers=local.docker('ps','-a','--filter','name=affiliate-full-api','--format','{{.Names}}').stdout.splitlines()
for name in containers:
 if not name.startswith('affiliate-full-api'):continue
 log=local.docker('logs',name,check=False);text=log.stdout+log.stderr
 if 'R-RETRY rabbitmq://affiliate-full-rabbit/affiliate-order-conversion' not in text:continue
 for line in text.splitlines():
  try:entry=json.loads(line)
  except Exception:continue
  if entry.get('State',{}).get('InputAddress')=='rabbitmq://affiliate-full-rabbit/affiliate-order-conversion' and 'R-RETRY' in entry.get('Message',''):
   retries.append({'timestampUtc':entry.get('Timestamp'),'queue':'affiliate-order-conversion','exceptionType':'TimeoutException','rawExceptionOmitted':True})
assert retries,'Retained forced-retry evidence missing'
data={'capturedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'readOnly':True,'outbox':rows,'consumerRetries':retries}
target=out/'isolated-delivery-evidence.json';assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
