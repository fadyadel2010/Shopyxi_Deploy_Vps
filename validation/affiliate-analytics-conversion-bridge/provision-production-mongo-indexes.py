"""Provision only the two reviewed analytics indexes; no business-document mutations."""
import datetime,json,subprocess
from pathlib import Path
out=Path(__file__).parent;script=(out/'production-mongo-indexes.js').read_text()
remote=r'''
import base64,json,re,subprocess
from urllib.parse import urlsplit
K='/usr/local/bin/kubectl'
def call(*a):return subprocess.check_output(a,text=True,timeout=90)
d=json.loads(call(K,'get','deployment','-n','shopixy-app','shopixy-api','-o','json'))
c=next(x for x in d['spec']['template']['spec']['containers'] if x['name']=='api')
e=next(x for x in c['env'] if x['name']=='MongoDB__ConnectionString')
r=e['valueFrom']['secretKeyRef'];s=json.loads(call(K,'get','secret','-n','shopixy-app',r['name'],'-o','json'))
uri=base64.b64decode(s['data'][r['key']]).decode()
dbname=urlsplit(uri).path.strip('/') or 'shopixy'
uri=re.sub(r'tlsCAFile=[^&]+','tlsCAFile=/tls/ca.crt',uri)
js=__SCRIPT__.replace('__DATABASE_NAME__',json.dumps(dbname))
run=subprocess.run([K,'exec','-n','shopixy-mongo','mongo-a-0','-c','mongod','--','mongosh',uri,'--quiet','--eval',js],capture_output=True,text=True,timeout=120)
if run.returncode:
 text=run.stdout+run.stderr
 categories=[name for name in ('Unauthorized','not authorized','AuthenticationFailed','SyntaxError','createIndexes','primary','index verification','InvalidIndexSpecificationOption','IndexOptionsConflict','connection') if name.lower() in text.lower()]
 text=text.replace(uri,'[connection-redacted]')
 text=re.sub(r'mongodb(?:\+srv)?://\S+','[connection-redacted]',text)
 print(json.dumps({'failedStage':'mongo-indexes','errorCategories':categories,'returnCode':run.returncode,'diagnostic':text[:1200]}))
 raise SystemExit(2)
print(run.stdout.strip())
'''.replace('__SCRIPT__',repr(script))
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=180)
if r.returncode:
 try: print(json.dumps(json.loads(r.stdout),indent=2))
 except Exception: pass
 raise SystemExit('Bounded Mongo index provisioning failed; raw output withheld')
data=json.loads(r.stdout);data['verifiedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
target=out/'production-mongo-indexes.json';assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
