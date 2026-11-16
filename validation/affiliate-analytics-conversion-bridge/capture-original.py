"""Read exact retained isolated authority without customer/session/credential export."""
import datetime
import importlib.util
import json
from pathlib import Path

out = Path(__file__).parent
spec = importlib.util.spec_from_file_location('local',out/'local-tools.py')
local = importlib.util.module_from_spec(spec); spec.loader.exec_module(local)
local.assert_isolated()
sql = '''BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object('orderId',o."Id",'tenantId',o."TenantId",'affiliateId',a."AffiliateAccountId",
 'attributionId',a."Id",'promotionLinkId',a."PromotionLinkId",'orderCreatedAt',o."CreatedAt",
 'orderSessionPresent',o."SessionId" IS NOT NULL,'cartId',o."CartId",
 'checkoutId',s."Id",'convertedOrderId',s."ConvertedOrderId",
 'sameOrderCheckoutSession',o."SessionId"=s."SessionId",'sameCart',o."CartId"=s."CartId",
 'journeys',jsonb_path_query_array(s."ItemsSnapshotJson"::jsonb,'$[*].affiliateJourney'))
FROM "Orders" o JOIN "AffiliateAttributions" a ON a."OrderId"=o."Id" AND a."TenantId"=o."TenantId"
LEFT JOIN "CheckoutSessions" s ON s."CartId"=o."CartId" AND s."TenantId"=o."TenantId"
WHERE o."Id"='8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12';
SELECT json_build_object('eventType',"EventType",'eventId',"Id",'processed',"ProcessedAtUtc" IS NOT NULL,'retryCount',"RetryCount")
FROM "OutboxMessages" WHERE "AggregateId"='8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12';
ROLLBACK;'''
rows = [json.loads(x) for x in local.query(sql).splitlines()]
mongo = json.loads(local.docker('exec','affiliate-full-mongo','mongosh','--quiet','--eval',
 "const d=db.getSiblingDB('affiliate_full_e2e');const daily=d.affiliate_analytics_daily.find({}).toArray();print(EJSON.stringify({clickCount:d.affiliate_clicks.countDocuments({}),click:d.affiliate_clicks.findOne({_id:'3de85e14-3e61-4b8d-a964-a49aeb3f1ba7'},{sessionId:0,ipHash:0,userAgent:0}),trackedClickEvents:daily.reduce((n,x)=>n+Number(x.clicks||0),0)}))").stdout)
receipt = {'capturedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'readOnly':True,
    'onlyExistingIsolatedFixture':True,'sql':rows,'mongo':mongo}
target = out/'original-finding-current.json'; assert not target.exists()
target.write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
