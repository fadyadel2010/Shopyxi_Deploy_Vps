"""Bind admitted images, archive integrity, runtime probes and exact deployment-script bytes."""
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import sys

root = Path(__file__).resolve().parents[2];out=Path(__file__).parent
api=json.loads((out/'production-api-candidate.json').read_text());web=json.loads((out/'production-web-candidate.json').read_text())
assert api['securityGatePassed'] and api['sourceInputsMatchStagedIndex'] and web['securityGatePassed']
assert json.loads((out/'gate-summary.json').read_text())['passed']
directory=root.parent/'publishing-readiness-production-web'
with tarfile.open(directory/'web.tar') as tar:
    rows=json.load(tar.extractfile('manifest.json'));assert len(rows)==1
    web['runtimeConfigId']='sha256:'+hashlib.sha256(tar.extractfile(rows[0]['Config']).read()).hexdigest()
archive=directory/'web.tar.gz'
if archive.exists():
    assert sys.argv[1:]==['--refresh-metadata'],'Preserve previous archive'
    previous=json.loads((out/'release-input.json').read_text())
    assert hashlib.sha256(archive.read_bytes()).hexdigest()==previous['components']['web']['archiveSha256']
else:
    assert not sys.argv[1:]
    with (directory/'web.tar').open('rb') as source,archive.open('xb') as destination:
        with gzip.GzipFile(fileobj=destination,mode='wb',mtime=0) as compressed:shutil.copyfileobj(source,compressed)
web['archiveSha256']=hashlib.sha256(archive.read_bytes()).hexdigest()
dist={item['path']:item['sha256'] for item in json.loads((directory/'dist-inputs.json').read_text())}
assemblies={item['filename']:item['sha256'] for item in api['assemblySha256']}
api['runtimeHashes']=[{'path':'/app/'+name,'sha256':value} for name,value in assemblies.items()]
api['probe']={'path':'/app/PointOfSale.API.dll','sha256':assemblies['PointOfSale.API.dll']}
web['runtimeHashes']=[{'path':'/usr/share/nginx/html/index.html','sha256':dist['index.html']}]
web['probe']={'path':'/usr/share/nginx/html/index.html','sha256':dist['index.html']}
data={'allGatesPassed':True,'components':{'api':api,'web':web},'deploymentScriptSha256':hashlib.sha256((out/'deploy-api-web.py').read_bytes()).hexdigest()}
(out/'release-input.json').write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps({'components':['api','web'],'allGatesPassed':True,'deploymentScriptBound':True,'storefrontChanged':False}))
