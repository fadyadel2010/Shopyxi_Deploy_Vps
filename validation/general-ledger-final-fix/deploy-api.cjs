const fs=require('fs'),path=require('path'),cp=require('child_process'),crypto=require('crypto');
const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const index=JSON.parse(fs.readFileSync(path.join(__dirname,'source-index.json')));
for(const [f,hash] of Object.entries(index.files))if(sha(path.join(index.stage,f))!==hash)throw Error('Reviewed release source changed: '+f);
if(fs.existsSync(path.join(__dirname,'deployment.json')))throw Error('Deployment receipt already exists; no repeated deployment');
if(!/failed="0"/.test(fs.readFileSync(path.join(__dirname,'after.trx'),'utf8')))throw Error('Focused tests must pass');
const shippedStock=JSON.parse(fs.readFileSync(path.join(__dirname,'stock-message-image-after.json')));
if(shippedStock.status!==409||shippedStock.code!=='STOCK_COUNT_STALE'||!shippedStock.dataRedacted)throw Error('Shipped static error-contract gate failed');
if(fs.readFileSync(path.join(__dirname,'api-unrelated-parity.diff'),'utf8').trim())throw Error('Unrelated runtime changes');
const scan=JSON.parse(fs.readFileSync(path.join(__dirname,'api-trivy-high-critical.json')));
if((scan.Results||[]).some(r=>(r.Vulnerabilities||[]).length))throw Error('Image vulnerability gate failed');
const old='docker.io/shopixy/api@sha256:fcdc623d757ae9f2ddf2314b8b6b81b0c993d85240c846ebffd6206fd2e468c0';
const ref=fs.readFileSync(path.join(__dirname,'api-image-reference.txt'),'utf8').trim();
if(!/^docker\.io\/shopixy\/api@sha256:[a-f0-9]{64}$/.test(ref))throw Error('Immutable API identity required');
const archive='C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-general-ledger-final-fix/artifacts/api.tar.gz';
const archiveSha256=sha(archive),remote='/var/lib/shopixy/general-ledger-final-fix-20261114/api.tar.gz';
const ssh=(py,timeout=120000)=>cp.execFileSync('ssh',['-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','shopixy-vps','python3 -'],{input:py,encoding:'utf8',timeout,maxBuffer:6*1024*1024});
ssh(`from pathlib import Path\np=Path(${JSON.stringify(path.posix.dirname(remote))});p.mkdir(mode=0o700,exist_ok=False)\n`);
cp.execFileSync('scp',['-q','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',archive,'shopixy-vps:'+remote],{timeout:600000});
const program=`import subprocess,json,hashlib,pathlib
new=${JSON.stringify(ref)}
old=${JSON.stringify(old)}
p=pathlib.Path(${JSON.stringify(remote)})
assert hashlib.sha256(p.read_bytes()).hexdigest()==${JSON.stringify(archiveSha256)}
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
receipt={'component':'API','before':before,'intendedImage':new,'archiveSha256':${JSON.stringify(archiveSha256)},'rollbackImage':old,'imageOnly':True,'schemaOrInfrastructureChanged':False,'unrelatedDeploymentSpecsBefore':unrelatedBefore}
print(json.dumps({'stage':'before',**receipt}),flush=True)
kube(*args)
kube('rollout','status','deployment/shopixy-api','-n','shopixy-app','--timeout=480s',timeout=510)
after=snapshot();assert ready(after) and after['uid']==before['uid'] and after['image']==new and after['specSha256']==before['expectedImageOnlySpecSha256']
allowed={identity['id']}|{s.split('@')[-1] for s in identity.get('repoDigests',[])}
container=next(c for c in after['pods'][0]['containers'] if c['name']=='api');assert container['ready'] and container['imageID'].split('@')[-1] in allowed
unrelatedAfter=unrelated();assert unrelatedBefore==unrelatedAfter
print(json.dumps({'stage':'verified',**receipt,'after':after,'runtimeImageId':identity['id'],'verifiedRepoDigests':identity.get('repoDigests',[]),'immutableIdentityVerified':True,'unrelatedDeploymentSpecsAfter':unrelatedAfter,'unrelatedDeploymentsUnchanged':True}),flush=True)
`;
fs.writeFileSync(path.join(__dirname,'deployment-driver.py'),program,{flag:'wx'});
let output;try{output=ssh(program,900000);}catch(e){fs.writeFileSync(path.join(__dirname,'deployment-failed.log'),(e.stdout||'')+(e.stderr||'')+e.message,{flag:'wx'});throw e;}
fs.writeFileSync(path.join(__dirname,'deployment.log'),output,{flag:'wx'});
const receipt=output.trim().split(/\r?\n/).filter(s=>s.startsWith('{')).map(s=>JSON.parse(s)).find(r=>r.stage==='verified');
if(!receipt)throw Error('Verified deployment receipt missing');
fs.writeFileSync(path.join(__dirname,'deployment.json'),JSON.stringify(receipt,null,2),{flag:'wx'});
console.log(JSON.stringify({image:ref,ready:true,immutableIdentityVerified:true,APIOnly:true,unrelatedDeploymentsUnchanged:true}));
