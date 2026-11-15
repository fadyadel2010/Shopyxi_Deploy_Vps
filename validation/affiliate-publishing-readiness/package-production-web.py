"""Immutable Web packaging with exact build-input and dist seals; no deployment."""
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
spec = importlib.util.spec_from_file_location('stack',root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec); spec.loader.exec_module(stack)
context = root.parent/'publishing-readiness-production-web'
assert not context.exists(), 'Preserve prior packaging evidence'
base = 'shopixy/web:publishing-readiness-verified-base'
expected = 'sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991'
image = json.loads(stack.docker('image','inspect',base).stdout)[0]
assert image['Id'] == expected and image['Config']['User'] not in ('','0','root','0:0')
names = subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=root).decode().strip('\0').split('\0')
inputs = sorted({name for name in names if name.startswith(('PointOfSale.Web/','PointOfSale.Storefront/','PointOfSale.CommerceHub/','theme-authority/'))})
manifest = [{'path':name,'sha256':hashlib.sha256((root/name).read_bytes()).hexdigest()} for name in inputs]
source_seal = hashlib.sha256(json.dumps(manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest()
context.mkdir()
dist = root.parent/'publishing-readiness-web/dist'
assert dist.is_dir()
shutil.copytree(dist,context/'dist')
dist_manifest = [{'path':file.relative_to(dist).as_posix(),'sha256':hashlib.sha256(file.read_bytes()).hexdigest()} for file in sorted(dist.rglob('*')) if file.is_file()]
dist_seal = hashlib.sha256(json.dumps(dist_manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest()
(context/'Dockerfile').write_text('FROM '+base+'\nLABEL shopixy.io/source-input-sha256="'+source_seal+'" shopixy.io/web-dist-sha256="'+dist_seal+'"\nUSER 0\nRUN rm -rf /usr/share/nginx/html/*\nCOPY dist/ /usr/share/nginx/html/\nUSER '+image['Config']['User']+'\n',encoding='utf-8',newline='\n')
(context/'source-inputs.json').write_text(json.dumps(manifest,indent=2)+'\n')
(context/'dist-inputs.json').write_text(json.dumps(dist_manifest,indent=2)+'\n')
tag = 'shopixy/web:affiliate-publishing-readiness-'+source_seal[:12]
result = stack.docker('build','--network=none','--pull=false','-t',tag,stack.WSL_BASE+'/'+context.name,check=False)
(root.parent/'publishing-readiness-production-web-build-private.log').write_text(result.stdout+result.stderr)
assert result.returncode == 0
built = json.loads(stack.docker('image','inspect',tag).stdout)[0]
reference = 'docker.io/shopixy/web@'+built['Id']
assert stack.docker('run','--rm','--network=none','--entrypoint','/usr/bin/id',tag,'-u').stdout.strip() not in ('0','')
stack.docker('run','--rm','--network=none','--entrypoint','nginx',tag,'-t')
for item in dist_manifest:
    actual = stack.docker('run','--rm','--network=none','--entrypoint','/usr/bin/sha256sum',tag,'/usr/share/nginx/html/'+item['path']).stdout.split()[0]
    assert actual == item['sha256']
scan = subprocess.run(['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--exec','env','DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock','DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker','/opt/shopixy-toolchain/bin/trivy','image','--scanners','vuln','--severity','HIGH,CRITICAL','--exit-code','1','--format','json','--output',stack.WSL_BASE+'/'+context.name+'/trivy.json',tag],capture_output=True,text=True,timeout=600)
(root.parent/'publishing-readiness-production-web-security-private.log').write_text(scan.stdout+scan.stderr)
assert scan.returncode == 0, 'Web security gate failed; do not deploy'
report = json.loads((context/'trivy.json').read_text())
assert report['Metadata']['ImageID'] == built['Id']
assert not [v for group in report['Results'] for v in group.get('Vulnerabilities',[]) if v['Severity'] in ('HIGH','CRITICAL')]
stack.docker('save','-o',stack.WSL_BASE+'/'+context.name+'/web.tar','shopixy/web@'+built['Id'])
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'component':'web','image':reference,'tag':tag,'baseImage':expected,'sourceInputSha256':source_seal,'sourceInputCount':len(inputs),'distSha256':dist_seal,'distFiles':len(dist_manifest),'imageContentsMatchBuild':True,'nonRoot':True,'nginxConfigValid':True,'trivyHighCriticalFindings':0,'securityGatePassed':True,'productionDeployment':False,'deliveryMethod':'not delivered; local archive only'}
(out/'production-web-candidate.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
