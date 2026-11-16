"""Read-only Mongo pod shape discovery; emits no env/Secret values."""
import json,subprocess
remote=r'''
import json,subprocess
p=json.loads(subprocess.check_output(['/usr/local/bin/kubectl','get','pod','-n','shopixy-mongo','mongo-a-0','-o','json'],text=True))
c=next(x for x in p['spec']['containers'] if x['name']=='mongod')
print(json.dumps({'args':c.get('args',[]),'envNames':[x['name'] for x in c.get('env',[])],
 'mounts':[x['mountPath'] for x in c.get('volumeMounts',[])]},indent=2))
d=json.loads(subprocess.check_output(['/usr/local/bin/kubectl','get','deployment','-n','shopixy-app','shopixy-api','-o','json'],text=True))
a=next(x for x in d['spec']['template']['spec']['containers'] if x['name']=='api')
print(json.dumps({'apiEnv':[{'name':x['name'],'from':list(x.get('valueFrom',{}))} for x in a.get('env',[]) if 'MONGO' in x['name'].upper()]},indent=2))
'''
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=120)
assert r.returncode==0,'Read-only discovery failed; raw output withheld'
print(r.stdout)
