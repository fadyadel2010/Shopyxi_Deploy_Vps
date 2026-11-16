"""Build and gate immutable Web bytes over the exact current Production base; no deployment."""
import datetime,hashlib,importlib.util,json,shutil,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(__file__).parent;index=1;context=root.parent/'analytics-production-web'
while context.exists():index+=1;context=root.parent/f'analytics-production-web-{index}'
context.mkdir()
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)
base='docker.io/shopixy/web@sha256:ee6f3d73e076221dd600ea44c178afc8b811e2a32758eae9890553cd38486abd'
image=json.loads(local.docker('image','inspect',base).stdout)[0];assert image['Id'].endswith('ee6f3d73e076221dd600ea44c178afc8b811e2a32758eae9890553cd38486abd')
assert image['Config']['User'] not in ('','0','root','0:0')
names=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=root).decode().strip('\0').split('\0')
inputs=sorted({n for n in names if n.startswith(('PointOfSale.Web/','PointOfSale.Storefront/','PointOfSale.CommerceHub/','theme-authority/'))})
manifest=[{'path':n,'sha256':hashlib.sha256((root/n).read_bytes()).hexdigest()} for n in inputs]
source_seal=hashlib.sha256(json.dumps(manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest()
dist=root.parent/'analytics-web/dist';assert dist.is_dir();shutil.copytree(dist,context/'dist')
dist_manifest=[{'path':f.relative_to(dist).as_posix(),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in sorted(dist.rglob('*')) if f.is_file()]
dist_seal=hashlib.sha256(json.dumps(dist_manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest()
(context/'Dockerfile').write_text('FROM '+base+'\nLABEL shopixy.io/source-input-sha256="'+source_seal+'" shopixy.io/web-dist-sha256="'+dist_seal+'"\nUSER 0\nRUN rm -rf /usr/share/nginx/html/*\nCOPY dist/ /usr/share/nginx/html/\nUSER '+image['Config']['User']+'\n',encoding='utf-8',newline='\n')
(context/'source-inputs.json').write_text(json.dumps(manifest,indent=2)+'\n');(context/'dist-inputs.json').write_text(json.dumps(dist_manifest,indent=2)+'\n')
tag='shopixy/web:affiliate-analytics-production-'+source_seal[:12]
build=local.docker('build','--network=none','--pull=false','-t',tag,local.WSL_BASE+'/'+context.name,check=False,timeout=600)
(root.parent/'analytics-production-web-build-private.log').write_text(build.stdout+build.stderr);assert build.returncode==0
built=json.loads(local.docker('image','inspect',tag).stdout)[0];reference='shopixy/web@'+built['Id']
assert local.docker('run','--rm','--network=none','--entrypoint','/usr/bin/id',tag,'-u').stdout.strip() not in ('0','')
local.docker('run','--rm','--network=none','--entrypoint','nginx',tag,'-t')
for item in dist_manifest:
 actual=local.docker('run','--rm','--network=none','--entrypoint','/usr/bin/sha256sum',tag,'/usr/share/nginx/html/'+item['path']).stdout.split()[0];assert actual==item['sha256']
scan=subprocess.run(['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--exec','env','DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock','DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker','/opt/shopixy-toolchain/bin/trivy','image','--scanners','vuln','--severity','HIGH,CRITICAL','--exit-code','1','--format','json','--output',local.WSL_BASE+'/'+context.name+'/trivy.json',tag],capture_output=True,text=True,timeout=600)
(root.parent/'analytics-production-web-security-private.log').write_text(scan.stdout+scan.stderr);assert scan.returncode==0
report=json.loads((context/'trivy.json').read_text());assert report['Metadata']['ImageID']==built['Id'] and not [v for x in report.get('Results',[]) for v in x.get('Vulnerabilities',[]) if v.get('Severity') in ('HIGH','CRITICAL')]
local.docker('save','-o',local.WSL_BASE+'/'+context.name+'/web.tar',reference);archive_sha=hashlib.sha256((context/'web.tar').read_bytes()).hexdigest()
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'component':'web','image':reference,'baseImage':image['Id'],'sourceInputSha256':source_seal,'sourceInputCount':len(inputs),'distSha256':dist_seal,'distFiles':len(dist_manifest),'archiveSha256':archive_sha,'imageContentsMatchBuild':True,'nonRoot':True,'nginxConfigValid':True,'trivyHighCriticalFindings':0,'securityGatePassed':True,'productionDeployment':False}
target=out/('production-web-candidate.json' if index==1 else f'production-web-candidate-{index}.json')
assert not target.exists();target.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt,indent=2))
