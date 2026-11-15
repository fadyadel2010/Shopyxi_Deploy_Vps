"""Build immutable isolated API/Web candidates from compiled inputs; never touches Production or Storefront."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import sys

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
spec = importlib.util.spec_from_file_location('local_stack', root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec); spec.loader.exec_module(stack)
kind = sys.argv[1]
assert kind in ('api','web')
base = {'api':('shopixy/api:affiliate-full-e2e-20261115','sha256:8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02'),
    'web':('shopixy/web@sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991','sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991')}[kind]
observed = json.loads(stack.docker('image','inspect',base[0]).stdout)[0]
assert observed['Id'] == base[1]
context = root.parent/('publishing-readiness-local-'+kind)
context.mkdir(exist_ok=True)
if kind == 'api':
    for name in ('PointOfSale.API.dll','PointOfSale.Core.dll','PointOfSale.Infrastructure.dll'):
        shutil.copy2(root/'PointOfSale.API/bin/Release/net10.0'/name,context/name)
    instructions = 'COPY --chown=1654:1654 *.dll /app/\nUSER 1654:1654\n'
else:
    # Use a verified local alias to avoid private-registry digest resolution.
    alias = 'shopixy/web:publishing-readiness-verified-base'
    existing = stack.docker('image','inspect',alias,check=False)
    if existing.returncode: stack.docker('tag',base[0],alias)
    else: assert json.loads(existing.stdout)[0]['Id'] == base[1]
    base = (alias,base[1])
    shutil.copytree(root.parent/'publishing-readiness-web/dist',context/'dist',dirs_exist_ok=True)
    instructions = 'USER 0\nRUN rm -rf /usr/share/nginx/html/*\nCOPY dist/ /usr/share/nginx/html/\nUSER '+observed['Config']['User']+'\n'
(context/'Dockerfile').write_text('FROM '+base[0]+'\n'+instructions,encoding='utf-8',newline='\n')
seal = hashlib.sha256(b''.join(path.relative_to(context).as_posix().encode()+hashlib.sha256(path.read_bytes()).digest()
    for path in sorted(context.rglob('*')) if path.is_file())).hexdigest()
tag = 'shopixy/'+kind+':affiliate-readiness-'+seal[:12]
result = stack.docker('build','--network=none','--pull=false','-t',tag,
    stack.WSL_BASE+'/'+context.name,check=False)
(root.parent/('publishing-readiness-local-'+kind+'-build-private.log')).write_text(result.stdout+result.stderr)
assert result.returncode == 0, 'Isolated image construction failed; inspect private build diagnostic'
image = json.loads(stack.docker('image','inspect',tag).stdout)[0]
receipt = {'component':kind,'imageId':image['Id'],'tag':tag,'baseImageId':base[1],'buildContextSha256':seal,
    'networkDuringBuild':'none','productionDeployment':False,'storefrontChanged':False}
(out/('local-'+kind+'-candidate.json')).write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(receipt,indent=2))
