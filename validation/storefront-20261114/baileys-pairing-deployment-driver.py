import subprocess,json,hashlib,pathlib
D={"deployment":"shopixy-baileys","container":"baileys","old":"docker.io/shopixy/baileys@sha256:7d1841c19ccdd8d654814b4f3a9d6420a655540103c21ad7bd90dcdfc9820e41","artifact":"C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-baileys-pairing/baileys-pairing.tar.gz"}
new="docker.io/shopixy/baileys@sha256:fb31e748e0262895030f609b6cb2edd50ea2a1703d5d5e6010d01c6e1988d3c4"
p=pathlib.Path("/var/lib/shopixy/storefront-20261114-app-only/baileys-pairing.tar.gz")
assert hashlib.sha256(p.read_bytes()).hexdigest()=="05a57351b6236997ca2ca249793b864144a0bca4659b3dcb21a4465214c3f6b0"
def command(args,timeout=180):
 r=subprocess.run(args,text=True,capture_output=True,timeout=timeout)
 if r.returncode: raise RuntimeError(r.stderr[-2000:])
 return r.stdout
def kube(*args,timeout=180):return command(['/usr/local/bin/kubectl','--request-timeout=30s',*args],timeout)
def image(ref):return json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',ref]))['status']
def snapshot(replacement=None):
 d=json.loads(kube('get','deployment',D['deployment'],'-n','shopixy-app','-o','json'));s=d['spec'];idx=next(i for i,c in enumerate(s['template']['spec']['containers']) if c['name']==D['container'])
 expected=json.loads(json.dumps(s))
 if replacement:expected['template']['spec']['containers'][idx]['image']=replacement
 hashed=lambda v:hashlib.sha256(json.dumps(v,sort_keys=True,separators=(',',':')).encode()).hexdigest()
 selector=','.join(k+'='+v for k,v in s['selector']['matchLabels'].items());pods=json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items'];pods=[p for p in pods if not p['metadata'].get('deletionTimestamp')]
 return {'deployment':D['deployment'],'uid':d['metadata']['uid'],'resourceVersion':d['metadata']['resourceVersion'],'generation':d['metadata']['generation'],'observedGeneration':d.get('status',{}).get('observedGeneration'),'index':idx,'image':s['template']['spec']['containers'][idx]['image'],'specSha256':hashed(s),'expectedImageOnlySpecSha256':hashed(expected),'replicas':s.get('replicas'),'strategy':s['strategy']['type'],**{k:d.get('status',{}).get(k,0) for k in ['readyReplicas','updatedReplicas','availableReplicas']},'pods':[{'name':p['metadata']['name'],'phase':p.get('status',{}).get('phase'),'containers':[{k:c.get(k) for k in ['name','imageID','ready','restartCount']} for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}
def ready(s):return s['replicas']==s['readyReplicas']==s['updatedReplicas']==s['availableReplicas']==1 and s['generation']==s['observedGeneration'] and len(s['pods'])==1
before=snapshot(new);assert ready(before) and before['image']==D['old'];image(D['old'])
command(['/usr/local/bin/k3s','ctr','images','import','--digests',str(p)],timeout=600)
try:identity=image(new)
except RuntimeError:
 command(['/usr/local/bin/k3s','ctr','images','tag',"docker.io/shopixy/baileys:storefront-20261114-pairing-watchdog",new]);identity=image(new)
assert new in identity.get('repoDigests',[])
imagepath='/spec/template/spec/containers/'+str(before['index'])+'/image'
patch=[{'op':'test','path':'/metadata/uid','value':before['uid']},{'op':'test','path':'/metadata/resourceVersion','value':before['resourceVersion']},{'op':'test','path':imagepath,'value':D['old']},{'op':'replace','path':imagepath,'value':new}]
args=['patch','deployment',D['deployment'],'-n','shopixy-app','--type=json','-p',json.dumps(patch)]
kube(*args,'--dry-run=server')
receipt={'before':before,'intendedImage':new,'archiveSha256':"05a57351b6236997ca2ca249793b864144a0bca4659b3dcb21a4465214c3f6b0",'rollbackImage':D['old'],'imageOnly':True,'schemaOrInfrastructureChanged':False}
print(json.dumps({'stage':'before',**receipt}),flush=True)
kube(*args)
kube('rollout','status','deployment/'+D['deployment'],'-n','shopixy-app','--timeout=480s',timeout=510)
after=snapshot();assert ready(after) and after['uid']==before['uid'] and after['image']==new and after['specSha256']==before['expectedImageOnlySpecSha256']
allowed={identity['id']}|{s.split('@')[-1] for s in identity.get('repoDigests',[])}
container=next(c for c in after['pods'][0]['containers'] if c['name']==D['container']);assert container['ready'] and container['imageID'].split('@')[-1] in allowed
print(json.dumps({'stage':'verified',**receipt,'after':after,'runtimeImageId':identity['id'],'verifiedRepoDigests':identity.get('repoDigests',[]),'immutableIdentityVerified':True}),flush=True)
