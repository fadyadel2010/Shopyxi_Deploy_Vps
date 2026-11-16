"""Overlay compiled application bytes on current accepted isolated images. No Production access."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import sys

out=Path(__file__).parent; root=out.parents[1]
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py')
local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
kind=sys.argv[1]; assert kind in ('api','web')
container=json.loads(local.docker('inspect','affiliate-full-'+kind).stdout)[0]
assert container['State']['Running'] and list(container['NetworkSettings']['Networks']) == [local.NETWORK]
base=container['Image']; alias='shopixy/'+kind+':analytics-accepted-base'
local.docker('tag',base,alias)
index=1
context=root.parent/('analytics-local-'+kind)
while context.exists():
    index+=1; context=root.parent/('analytics-local-'+kind+'-'+str(index))
context.mkdir()
if kind == 'api':
    for name in ('PointOfSale.API.dll','PointOfSale.Core.dll','PointOfSale.Infrastructure.dll'):
        shutil.copy2(root/'PointOfSale.API/bin/Release/net10.0'/name,context/name)
    instructions='COPY --chown=1654:1654 *.dll /app/\nUSER 1654:1654\n'
else:
    shutil.copytree(root.parent/'analytics-web/dist',context/'dist')
    user=json.loads(local.docker('image','inspect',base).stdout)[0]['Config']['User']
    instructions='USER 0\nRUN rm -rf /usr/share/nginx/html/*\nCOPY dist/ /usr/share/nginx/html/\nUSER '+user+'\n'
(context/'Dockerfile').write_text('FROM '+alias+'\n'+instructions,encoding='utf-8',newline='\n')
seal=hashlib.sha256(b''.join(p.relative_to(context).as_posix().encode()+hashlib.sha256(p.read_bytes()).digest()
    for p in sorted(context.rglob('*')) if p.is_file())).hexdigest()
tag='shopixy/'+kind+':affiliate-analytics-'+seal[:12]
result=local.docker('build','--network=none','--pull=false','-t',tag,local.WSL_BASE+'/'+context.name,check=False,timeout=600)
if result.returncode: raise SystemExit('Candidate build failed; private diagnostic retained')
image=json.loads(local.docker('image','inspect',tag).stdout)[0]
receipt={'component':kind,'tag':tag,'imageId':image['Id'],'baseImageId':base,'buildContextSha256':seal,
 'isolatedOnly':True,'networkDuringBuild':'none'}
name='local-'+kind+'-candidate'+('' if index==1 else '-'+str(index))+'.json'
(out/name).write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
