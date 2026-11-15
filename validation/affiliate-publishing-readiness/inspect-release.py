"""Read-only failed-release receipts and sanitized workload state; never retry a rollout."""
import json
from pathlib import Path
import subprocess

remote = r'''
import datetime,json,subprocess
from pathlib import Path
root=Path('/var/lib/shopixy/affiliate-publishing-readiness-20261115')
def kube(*args):
 r=subprocess.run(['/usr/local/bin/kubectl',*args],capture_output=True,text=True,check=True,timeout=60)
 return json.loads(r.stdout)
receipts={name:json.loads((root/name).read_text()) for name in ('release-started.json','release-failed.json','release-verified.json') if (root/name).exists()}
workloads=[]
for kind in ('api','web','storefront'):
 d=kube('get','deployment','shopixy-'+kind,'-n','shopixy-app','-o','json')
 selector=','.join(k+'='+v for k,v in d['spec']['selector']['matchLabels'].items())
 pods=kube('get','pods','-n','shopixy-app','-l',selector,'-o','json')['items']
 workloads.append({'component':kind,'generation':d['metadata']['generation'],'image':next(c['image'] for c in d['spec']['template']['spec']['containers'] if c['name']==kind),
 'status':d['status'],'pods':[{'name':p['metadata']['name'],'phase':p['status']['phase'],'deleting':bool(p['metadata'].get('deletionTimestamp')),
 'containers':[{k:c.get(k) for k in ('name','image','imageID','ready','restartCount','state','lastState')} for c in p['status'].get('containerStatuses',[])]} for p in pods]})
events=kube('get','events','-n','shopixy-app','--field-selector','type=Warning','-o','json')
safe_events=[{'reason':e.get('reason'),'message':e.get('message'),'object':e.get('involvedObject',{}).get('name'),'lastTimestamp':e.get('lastTimestamp')} for e in events['items'] if any(e.get('involvedObject',{}).get('name','').startswith('shopixy-'+k) for k in ('api','web'))]
print(json.dumps({'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'readOnly':True,'receipts':receipts,'workloads':workloads,'warningEvents':safe_events,'lastCommandError':(root/'last-command-error-private.log').read_text() if (root/'last-command-error-private.log').exists() else None}))
'''
result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=240)
assert result.returncode == 0,'Read-only release inspection failed; raw output withheld'
data=json.loads(result.stdout)
# Diagnostics may include container termination text; keep them outside the repository.
(Path(__file__).resolve().parents[3]/'publishing-readiness-release-inspection-private.json').write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps({'verifiedAtUtc':data['verifiedAtUtc'],'receipts':list(data['receipts']),
 'rollbackVerified':data['receipts'].get('release-failed.json',{}).get('rollbackVerified'),
 'workloads':[{k:w[k] for k in ('component','generation','image','status')} for w in data['workloads']],
 'warningEvents':data['warningEvents'],'lastCommandError':data['lastCommandError']},indent=2))
