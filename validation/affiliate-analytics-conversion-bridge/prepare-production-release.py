"""Seal and transfer already-gated candidates plus the fail-closed release runner."""
import hashlib,importlib.util,json,shutil,subprocess
from pathlib import Path
out=Path(__file__).parent;root=out.parents[1]
api_file=max(out.glob('production-api-candidate*.json'),key=lambda p:p.stat().st_mtime);web_file=max(out.glob('production-web-candidate*.json'),key=lambda p:p.stat().st_mtime)
api=json.loads(api_file.read_text());web=json.loads(web_file.read_text())
assert api['securityGatePassed'] and web['securityGatePassed'] and not api['productionDeployment'] and not web['productionDeployment']
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)
def tag_for(image,needle):
    tags=json.loads(local.docker('image','inspect',image).stdout)[0]['RepoTags']
    return next(t for t in tags if needle in t)
inputs={'api':{'archive':'api.tar','archiveSha256':api['archiveSha256'],'tag':tag_for(api['image'],'affiliate-analytics-production'),'sourceInputSha256':api['sourceInputSha256'],'oldImage':'docker.io/shopixy/api@sha256:39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b'},
 'web':{'archive':'web.tar','archiveSha256':web['archiveSha256'],'tag':tag_for(web['image'],'affiliate-analytics-production'),'sourceInputSha256':web['sourceInputSha256'],'oldImage':'docker.io/shopixy/web@sha256:ee6f3d73e076221dd600ea44c178afc8b811e2a32758eae9890553cd38486abd'},
 'storefrontImage':'docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df','migrationId':'20261115213516_AffiliateAnalyticsConversionBridge'}
if (out/'production-runtime-after.json').exists():
    current=json.loads((out/'production-runtime-after.json').read_text())
    for kind in ('api','web'):inputs[kind]['oldImage']=next(x['image'] for x in current['workloads'] if x['component']==kind)
index=1;staging=root.parent/'analytics-production-release'
while staging.exists(): index+=1;staging=root.parent/f'analytics-production-release-{index}'
staging.mkdir();api_context=next(p for p in root.parent.glob('analytics-production-api*') if p.is_dir() and (p/'api.tar').is_file() and hashlib.sha256((p/'api.tar').read_bytes()).hexdigest()==api['archiveSha256'])
web_context=next(p for p in root.parent.glob('analytics-production-web*') if p.is_dir() and (p/'web.tar').is_file() and hashlib.sha256((p/'web.tar').read_bytes()).hexdigest()==web['archiveSha256'])
shutil.copy2(api_context/'api.tar',staging/'api.tar');shutil.copy2(web_context/'web.tar',staging/'web.tar')
for name in ('production-schema.sql','production-schema-preflight.sql','production-schema-verify.sql','production-deploy-remote.py'): shutil.copy2(out/name,staging/name)
(staging/'release-inputs.json').write_text(json.dumps(inputs,indent=2)+'\n')
remote='/var/lib/shopixy/affiliate-analytics-20261116'+('' if index==1 else f'-r{index}')
(staging/'remote-directory.txt').write_text(remote+'\n')
manifest={f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in staging.iterdir() if f.is_file()}
(staging/'SHA256SUMS.json').write_text(json.dumps(manifest,indent=2)+'\n')
check=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','test','!','-e',remote],timeout=120);assert check.returncode==0,'Remote release directory already exists; inspect, do not overwrite'
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','mkdir','-m','0700',remote],check=True,timeout=120)
print(json.dumps({'remoteDirectory':remote,'filesStaged':len(list(staging.iterdir())),'sealed':True,'transferred':False,'deployed':False},indent=2))
