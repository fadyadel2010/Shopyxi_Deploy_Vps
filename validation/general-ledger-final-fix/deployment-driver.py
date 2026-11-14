import subprocess,json,hashlib,pathlib
new="docker.io/shopixy/api@sha256:d3f10ae2cc93eb1fdddb7bd89c55726ca3f6d0ea1e4dbbf7825f47aaa19dbcb9"
old="docker.io/shopixy/api@sha256:fcdc623d757ae9f2ddf2314b8b6b81b0c993d85240c846ebffd6206fd2e468c0"
p=pathlib.Path("/var/lib/shopixy/general-ledger-final-fix-20261114/api.tar.gz")
assert hashlib.sha256(p.read_bytes()).hexdigest()=="eebf0ea5c900ddda71995839cfcb2e33dbd99be7165e1958444e84091215f486"
def command(args,timeout=180):
 r=subprocess.run(args,text=True,capture_output=True,timeout=timeout)
 if r.returncode:raise RuntimeError(r.stderr[-2000:])
 return r.stdout
def kube(*args,timeout=180):return command(['/usr/local/bin/kubectl','--request-timeout=30s',*args],timeout)
def hashed(v):return hashlib.sha256(json.dumps(v,sort_keys=True,separators=(',',':')).encode()).hexdigest()
def image(ref):return json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',ref]))['status']
def unrelated():
 ds=json.loads(kube('get','deployment','-n','shopixy-app','-o','json'))['items']
 return {d['metadata']['name']:hashed(d['spec']) for d in ds if d['metadata']['name']!='shopixy-api'}
def snapshot(replacement=None):
 d=json.loads(kube('get','deployment','shopixy-api','-n','shopixy-app','-o','json'));s=d['spec'];idx=next(i for i,c in enumerate(s['template']['spec']['containers']) if c['name']=='api')
 expected=json.loads(json.dumps(s))
 if replacement:expected['template']['spec']['containers'][idx]['image']=replacement
 selector=','.join(k+'='+v for k,v in s['selector']['matchLabels'].items());pods=json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items'];pods=[p for p in pods if not p['metadata'].get('deletionTimestamp')]
 return {'uid':d['metadata']['uid'],'resourceVersion':d['metadata']['resourceVersion'],'generation':d['metadata']['generation'],'observedGeneration':d.get('status',{}).get('observedGeneration'),'index':idx,'image':s['template']['spec']['containers'][idx]['image'],'specSha256':hashed(s),'expectedImageOnlySpecSha256':hashed(expected),'replicas':s.get('replicas'),**{k:d.get('status',{}).get(k,0) for k in ['readyReplicas','updatedReplicas','availableReplicas']},'pods':[{'name':p['metadata']['name'],'phase':p.get('status',{}).get('phase'),'containers':[{k:c.get(k) for k in ['name','imageID','ready','restartCount']} for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}
def ready(s):return s['replicas']==s['readyReplicas']==s['updatedReplicas']==s['availableReplicas']==1 and s['generation']==s['observedGeneration'] and len(s['pods'])==1
before=snapshot(new);assert ready(before) and before['image']==old,'Current baseline differs; never overwrite a newer release'
unrelatedBefore=unrelated();image(old)
command(['/usr/local/bin/k3s','ctr','images','import','--digests',str(p)],timeout=600)
try:identity=image(new)
except RuntimeError:
 command(['/usr/local/bin/k3s','ctr','images','tag','docker.io/shopixy/api:general-ledger-final-fix-20261114',new]);identity=image(new)
assert new in identity.get('repoDigests',[])
imagepath='/spec/template/spec/containers/'+str(before['index'])+'/image'
patch=[{'op':'test','path':'/metadata/uid','value':before['uid']},{'op':'test','path':'/metadata/resourceVersion','value':before['resourceVersion']},{'op':'test','path':imagepath,'value':old},{'op':'replace','path':imagepath,'value':new}]
args=['patch','deployment','shopixy-api','-n','shopixy-app','--type=json','-p',json.dumps(patch)]
kube(*args,'--dry-run=server')
receipt={'component':'API','before':before,'intendedImage':new,'archiveSha256':"eebf0ea5c900ddda71995839cfcb2e33dbd99be7165e1958444e84091215f486",'rollbackImage':old,'imageOnly':True,'schemaOrInfrastructureChanged':False,'unrelatedDeploymentSpecsBefore':unrelatedBefore}
print(json.dumps({'stage':'before',**receipt}),flush=True)
kube(*args)
kube('rollout','status','deployment/shopixy-api','-n','shopixy-app','--timeout=480s',timeout=510)
after=snapshot();assert ready(after) and after['uid']==before['uid'] and after['image']==new and after['specSha256']==before['expectedImageOnlySpecSha256']
allowed={identity['id']}|{s.split('@')[-1] for s in identity.get('repoDigests',[])}
container=next(c for c in after['pods'][0]['containers'] if c['name']=='api');assert container['ready'] and container['imageID'].split('@')[-1] in allowed
unrelatedAfter=unrelated();assert unrelatedBefore==unrelatedAfter
print(json.dumps({'stage':'verified',**receipt,'after':after,'runtimeImageId':identity['id'],'verifiedRepoDigests':identity.get('repoDigests',[]),'immutableIdentityVerified':True,'unrelatedDeploymentSpecsAfter':unrelatedAfter,'unrelatedDeploymentsUnchanged':True}),flush=True)
