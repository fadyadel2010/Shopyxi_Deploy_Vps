"""Aggregate-only, read-only Production SQL/Mongo review. Never emits connection material or rows."""
import datetime,json,subprocess,sys
from pathlib import Path
out=Path(__file__).parent
remote=r'''
import base64,json,subprocess
K='/usr/local/bin/kubectl'
def call(*a,input=None): return subprocess.check_output(a,input=input,text=True,timeout=90)
d=json.loads(call(K,'get','deployment','-n','shopixy-app','shopixy-api','-o','json'))
c=next(x for x in d['spec']['template']['spec']['containers'] if x['name']=='api')
def value(name):
 e=next(x for x in c['env'] if x['name']==name)
 if 'value' in e:return e['value']
 r=e['valueFrom']['secretKeyRef'];s=json.loads(call(K,'get','secret','-n','shopixy-app',r['name'],'-o','json'))
 return base64.b64decode(s['data'][r['key']]).decode()
uri=value('MongoDB__ConnectionString')
from urllib.parse import urlsplit
dbname=urlsplit(uri).path.strip('/') or 'shopixy'
# The same cluster URI is valid from a replica-set member. Its CA path is API-specific;
# use the mounted Mongo CA while preserving TLS/auth/replica parameters.
import re
uri=re.sub(r'tlsCAFile=[^&]+','tlsCAFile=/tls/ca.crt',uri)
js="const d=db.getSiblingDB("+json.dumps(dbname)+");print(JSON.stringify({clickRecords:d.affiliate_clicks.countDocuments({}),convertedClicks:d.affiliate_clicks.countDocuments({orderId:{$ne:null},convertedAtUtc:{$ne:null}}),unconvertedClicks:d.affiliate_clicks.countDocuments({$or:[{orderId:null},{convertedAtUtc:null}]}),conversionDocuments:d.affiliate_order_conversions.countDocuments({})}))"
run=subprocess.run([K,'exec','-n','shopixy-mongo','mongo-a-0','-c','mongod','--','mongosh',uri,'--quiet','--eval',js],capture_output=True,text=True,timeout=90)
if run.returncode:
 print(json.dumps({'failedStage':'mongo','returnCode':run.returncode}));raise SystemExit(2)
mongo=run.stdout.strip()
sql="BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\nSELECT json_build_object('attributedOrders',count(*),'committedAttributedOrders',count(*) FILTER (WHERE o.\"Id\" IS NOT NULL)) FROM \"AffiliateAttributions\" a LEFT JOIN \"Orders\" o ON o.\"TenantId\"=a.\"TenantId\" AND o.\"Id\"=a.\"OrderId\";\nROLLBACK;"
pg=call(K,'exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','postgres','-d','app',input=sql).strip()
print(json.dumps({'sql':json.loads(pg),'mongo':json.loads(mongo),'readOnly':True,'personalDataExported':False}))
'''
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=180)
if r.returncode:
    try: print(json.dumps(json.loads(r.stdout),indent=2))
    except Exception: pass
    raise SystemExit('Production aggregate review failed; raw output withheld')
data=json.loads(r.stdout);data['capturedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
label=sys.argv[1] if len(sys.argv)==2 else 'before';assert label.replace('-','').isalnum()
target=out/f'production-candidates-{label}.json';assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
