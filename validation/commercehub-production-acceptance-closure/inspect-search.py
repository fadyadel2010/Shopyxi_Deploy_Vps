"""GET-only search diagnostics. Private connection settings stay in remote memory."""
import json
import subprocess
import datetime
import sys
from pathlib import Path

script = r'''
import base64,http.client,json,socket,ssl,subprocess,urllib.parse
def run(args):
    return subprocess.check_output(args).decode()
def kube(*args):
    return json.loads(run(['kubectl',*args,'-o','json']))
pod=kube('get','pods','-n','shopixy-app','-l','app=shopixy-api')['items'][0]['metadata']['name']
prefix=['kubectl','exec','-n','shopixy-app',pod,'-c','api','--']
env=dict(s.split('=',1) for s in run(prefix+['printenv']).splitlines() if '=' in s)
config=json.loads(run(prefix+['cat','/app/appsettings.json'])).get('Elasticsearch',{})
production=json.loads(run(prefix+['cat','/app/appsettings.Production.json'])).get('Elasticsearch',{})
config.update(production)
for key in ('Url','Username','Password','CaCertificatePath','CommerceHubProductsIndexName','Enabled'):
    if 'Elasticsearch__'+key in env: config[key]=env['Elasticsearch__'+key]
url=urllib.parse.urlparse(config['Url'])
services=kube('get','services','-A')['items']
service=next(s for s in services if url.hostname.split('.')[0]==s['metadata']['name'])
ip=service['spec']['clusterIP']
original=socket.getaddrinfo
def resolver(host,*args,**kwargs):
    return original(ip if host==url.hostname else host,*args,**kwargs)
socket.getaddrinfo=resolver
ca=run(prefix+['cat',config['CaCertificatePath']]) if config.get('CaCertificatePath') else None
context=ssl.create_default_context(cadata=ca)
connection=http.client.HTTPSConnection(url.hostname,url.port or 443,context=context,timeout=30)
headers={}
if config.get('Username'):
    headers['Authorization']='Basic '+base64.b64encode((config['Username']+':'+config.get('Password','')).encode()).decode()
index=config.get('CommerceHubProductsIndexName','commercehub_products')
results={'enabled':str(config.get('Enabled',True)).lower()=='true','index':index,'requests':[]}
for suffix in ('/_count','/_mapping'):
    connection.request('GET','/'+index+suffix,headers=headers)
    response=connection.getresponse()
    body=json.loads(response.read())
    record={'path':'/'+index+suffix,'status':response.status}
    if suffix=='/_count': record['count']=body.get('count')
    if suffix=='/_mapping' and response.status==200:
        record['indices']=list(body)
        record['fields']={name:sorted(value.get('mappings',{}).get('properties',{})) for name,value in body.items()}
    if response.status>=400: record['errorType']=body.get('error',{}).get('type')
    results['requests'].append(record)
print(json.dumps(results))
'''
result = subprocess.run(["ssh", "-o", "BatchMode=yes", "shopixy-vps", "python3 -"],
                        input=script.encode(), capture_output=True)
if result.returncode:
    # Do not print the private runtime traceback: exception text may contain endpoints.
    raise SystemExit("Read-only search diagnostic failed; private traceback not persisted.")
data = json.loads(result.stdout)
data["readOnly"] = True
data["verifiedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
out = Path(__file__).resolve().parent / ("search-final-readonly.json" if "--final" in sys.argv else "search-readonly.json")
out.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
print(json.dumps(data, indent=2))
