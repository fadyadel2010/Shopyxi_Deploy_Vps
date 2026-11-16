"""Read-only SQL/Mongo cross-check after application projector; no private session fields."""
import datetime,importlib.util,json,sys
from pathlib import Path
out=Path(__file__).parent
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
stage=sys.argv[1]; assert stage in ('post-historical-repair','post-new-order','post-replay','post-recovery','post-forced-retry')
sql='''BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_agg(json_build_object('orderId',a."OrderId",'attributionId',a."Id",'tenantId',a."TenantId",
 'affiliateId',a."AffiliateAccountId",'clickId',a."ReferralClickId",'productId',a."ReferralProductId",
 'orderCreatedAt',o."CreatedAt",'orderStatus',o."Status",'paymentStatus',o."PaymentStatus"))
FROM "AffiliateAttributions" a JOIN "Orders" o ON o."Id"=a."OrderId" AND o."TenantId"=a."TenantId";
ROLLBACK;'''
authority=json.loads(local.query(sql))
mongo=json.loads(local.docker('exec','affiliate-full-mongo','mongosh','--quiet','--eval',
 "const d=db.getSiblingDB('affiliate_full_e2e');print(EJSON.stringify({clicks:d.affiliate_clicks.find({},{sessionId:0,ipHash:0,userAgent:0}).toArray(),conversions:d.affiliate_order_conversions.find({}).toArray()}))").stdout)
receipt={'capturedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stage':stage,'readOnly':True,
 'sqlAuthority':authority,'mongoProjection':mongo}
target=out/(stage+'.json');assert not target.exists();target.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt,indent=2))
