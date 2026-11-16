"""Fail-closed single-node release runner. Execute only from the sealed remote release directory."""
import copy,datetime,hashlib,json,subprocess,tarfile,sys
from pathlib import Path

ROOT=Path(__file__).resolve().parent
inputs=json.loads((ROOT/'release-inputs.json').read_text())
K='/usr/local/bin/kubectl'
def command(args,timeout=180,input_text=None):
    r=subprocess.run(args,input=input_text,text=True,capture_output=True,timeout=timeout)
    if r.returncode: raise RuntimeError('release command failed: '+args[0]+' '+(args[1] if len(args)>1 else ''))
    return r.stdout
def kube(*args,timeout=180,input_text=None): return command([K,'--request-timeout=30s',*args],timeout,input_text)
def seal(value): return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()
def deployment(name,expected_image=None,replacement=None):
    d=json.loads(kube('get','deployment',name,'-n','shopixy-app','-o','json'));spec=d['spec']
    index=next(i for i,c in enumerate(spec['template']['spec']['containers']) if c['name'] in ('api','web','storefront'))
    image=spec['template']['spec']['containers'][index]['image']
    if expected_image is not None: assert image==expected_image,(name,image)
    expected=copy.deepcopy(spec)
    if replacement: expected['template']['spec']['containers'][index]['image']=replacement
    selector=','.join(k+'='+v for k,v in spec['selector']['matchLabels'].items())
    pods=[p for p in json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items'] if not p['metadata'].get('deletionTimestamp')]
    return {'name':name,'uid':d['metadata']['uid'],'resourceVersion':d['metadata']['resourceVersion'],'generation':d['metadata']['generation'],
      'observedGeneration':d.get('status',{}).get('observedGeneration'),'index':index,'image':image,'specSha256':seal(spec),
      'expectedImageOnlySpecSha256':seal(expected),'replicas':spec.get('replicas'),
      **{k:d.get('status',{}).get(k,0) for k in ('readyReplicas','updatedReplicas','availableReplicas')},
      'pods':[{'name':p['metadata']['name'],'containers':[{k:c.get(k) for k in ('name','imageID','ready','restartCount')} for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}
def ready(s): return s['replicas']==s['readyReplicas']==s['updatedReplicas']==s['availableReplicas']==1 and s['generation']==s['observedGeneration'] and len(s['pods'])==1 and all(c['ready'] for c in s['pods'][0]['containers'])
def import_image(component):
    item=inputs[component];archive=ROOT/item['archive'];assert hashlib.sha256(archive.read_bytes()).hexdigest()==item['archiveSha256']
    with tarfile.open(archive) as bundle:
        index_bytes=bundle.extractfile('index.json').read()
        archive_index=json.loads(index_bytes)
    wrapper='sha256:'+hashlib.sha256(index_bytes).hexdigest()
    wrapper_alias='docker.io/library/import-2026-11-16@'+wrapper
    command(['/usr/local/bin/k3s','ctr','images','import','--digests','--index-name',wrapper_alias,str(archive)],600)
    manifest=archive_index['manifests'][0]['digest']
    assert manifest.startswith('sha256:') and len(manifest)==71
    listing=command(['/usr/local/bin/k3s','ctr','images','list'])
    line=next(x for x in listing.splitlines() if manifest in x.split()[1:])
    normalized=line.split()[0]
    immutable='docker.io/shopixy/'+component+'@'+manifest
    try: identity=json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',immutable]))['status']
    except RuntimeError:
        command(['/usr/local/bin/k3s','ctr','images','tag',normalized,immutable]);identity=json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',immutable]))['status']
    inspected=json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',immutable]));labels=inspected.get('info',{}).get('imageSpec',{}).get('config',{}).get('Labels',{})
    assert labels.get('shopixy.io/source-input-sha256')==item['sourceInputSha256']
    assert identity['id']==inspected['status']['id']
    refs={row.split()[0]:row.split()[2] for row in command(['/usr/local/bin/k3s','ctr','images','list']).splitlines()[1:] if row.split()}
    for ref in inspected['status'].get('repoDigests',[]):
        if ref not in refs and ref.split('@')[1]==manifest:
            command(['/usr/local/bin/k3s','ctr','images','tag',immutable,ref])
    refs={row.split()[0]:row.split()[2] for row in command(['/usr/local/bin/k3s','ctr','images','list']).splitlines()[1:] if row.split()}
    assert all(refs.get(ref)==ref.split('@')[1] for ref in inspected['status'].get('repoDigests',[])), 'CRI cached alias unresolved'
    return immutable,identity['id']
def patch_image(before,replacement):
    current=deployment(before['name']);assert current['uid']==before['uid'] and current['specSha256']==before['specSha256'],'deployment changed independently'
    path='/spec/template/spec/containers/'+str(before['index'])+'/image'
    patch=[{'op':'test','path':'/metadata/uid','value':before['uid']},{'op':'test','path':'/metadata/resourceVersion','value':current['resourceVersion']},{'op':'test','path':path,'value':before['image']},{'op':'replace','path':path,'value':replacement}]
    args=['patch','deployment',before['name'],'-n','shopixy-app','--type=json','-p',json.dumps(patch)]
    kube(*args,'--dry-run=server');kube(*args);kube('rollout','status','deployment/'+before['name'],'-n','shopixy-app','--timeout=480s',timeout=510)
def rollback(name,new_image,old_image):
    try:
        current=deployment(name)
        if current['image']==new_image: patch_image(current,old_image)
    except Exception: pass

resume=sys.argv[1:] in (['--resume-schema-verified'],['--schema-already-verified'])
retry_index=1
while (ROOT/f'release-retry-{retry_index}-started.json').exists(): retry_index+=1
assert not (ROOT/'release-verified.json').exists()
if resume:
    if sys.argv[1:]==['--resume-schema-verified']:assert (ROOT/'release-failed.json').exists()
    else:assert not (ROOT/'release-started.json').exists() and retry_index==1
else: assert not (ROOT/'release-started.json').exists()
assert len(json.loads(kube('get','nodes','-o','json'))['items'])==1
before_api=deployment('shopixy-api',inputs['api']['oldImage']);before_web=deployment('shopixy-web',inputs['web']['oldImage']);before_storefront=deployment('shopixy-storefront',inputs['storefrontImage'])
assert ready(before_api) and ready(before_web) and ready(before_storefront)
preflight=kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','postgres','-d','app',input_text=(ROOT/('production-schema-verify.sql' if resume else 'production-schema-preflight.sql')).read_text()).strip().splitlines()
assert preflight==['t']
api_ref,api_id=import_image('api');web_ref,web_id=import_image('web')
before_api=deployment('shopixy-api',inputs['api']['oldImage'],api_ref)
before_web=deployment('shopixy-web',inputs['web']['oldImage'],web_ref)
assert ready(before_api) and ready(before_web)
receipt={'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'inputs':inputs,'before':{'api':before_api,'web':before_web,'storefront':before_storefront},'intended':{'api':api_ref,'web':web_ref},'schemaPreflightPassed':True,'businessDataWrites':False}
(ROOT/(f'release-retry-{retry_index}-started.json' if resume else 'release-started.json')).write_text(json.dumps(receipt,indent=2)+'\n')
if not resume:
    kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-v','ON_ERROR_STOP=1','-U','postgres','-d','app',input_text=(ROOT/'production-schema.sql').read_text())
verify=kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','postgres','-d','app',input_text=(ROOT/'production-schema-verify.sql').read_text()).strip().splitlines()
assert verify==['t']
try:
    patch_image(before_api,api_ref);patch_image(before_web,web_ref)
    after_api=deployment('shopixy-api',api_ref);after_web=deployment('shopixy-web',web_ref);after_storefront=deployment('shopixy-storefront',inputs['storefrontImage'])
    assert ready(after_api) and ready(after_web) and ready(after_storefront)
    assert after_api['specSha256']==before_api['expectedImageOnlySpecSha256'] and after_web['specSha256']==before_web['expectedImageOnlySpecSha256'] and after_storefront['specSha256']==before_storefront['specSha256']
    receipt.update({'after':{'api':after_api,'web':after_web,'storefront':after_storefront},'runtimeConfigIds':{'api':api_id,'web':web_id},'schemaVerified':True,'completedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()})
    (ROOT/'release-verified.json').write_text(json.dumps(receipt,indent=2)+'\n')
except Exception as ex:
    rollback('shopixy-web',web_ref,before_web['image']);rollback('shopixy-api',api_ref,before_api['image'])
    (ROOT/(f'release-retry-{retry_index}-failed.json' if resume else 'release-failed.json')).write_text(json.dumps({'failedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'errorType':type(ex).__name__},indent=2)+'\n')
    raise
print(json.dumps({'api':api_ref,'web':web_ref,'schemaVerified':True,'apiReady':True,'webReady':True,'storefrontUnchanged':True}))
