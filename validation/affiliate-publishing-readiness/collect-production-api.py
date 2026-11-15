"""Admit the full immutable API build only after index, runtime and security parity."""
import datetime
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile

root = Path(__file__).resolve().parents[2]
candidate = root.parent/'publishing-readiness-production-api'
build = candidate/'deploy/automatic-subdomains'
out = Path(__file__).parent
sha = lambda value: hashlib.sha256(value).hexdigest()
git = lambda *args: subprocess.check_output(['git',*args],cwd=root)
manifest = (build/'source-input-SHA256SUMS.txt').read_bytes()
entries = [line.split('  ',1) for line in manifest.decode().splitlines()]
batch = subprocess.run(['git','cat-file','--batch'],cwd=root,input=''.join(':'+name+'\n' for _,name in entries).encode(),capture_output=True,check=True).stdout
offset = 0
for expected,name in entries:
    assert sha((candidate/name).read_bytes()) == expected
    end = batch.index(b'\n',offset)
    length = int(batch[offset:end].split()[2])
    assert sha(batch[end+1:end+1+length]) == expected, 'Index/build mismatch: '+name
    offset = end+1+length+1
assert (candidate/'theme-authority/shopixy-theme-authority.json').read_bytes() == git('show',':theme-authority/shopixy-theme-authority.json')
audit = (build/'successor-nuget-vulnerability-audit.txt').read_bytes()
assert b'has no vulnerable packages' in audit
scan_bytes = (build/'successor-trivy-high-critical.json').read_bytes()
scan = json.loads(scan_bytes)
findings = [v for group in scan['Results'] for v in group.get('Vulnerabilities',[]) if v['Severity'] in ('HIGH','CRITICAL')]
assert scan['Results'] and not findings
image = (build/'successor-image-reference.txt').read_text().strip()
inspection = json.loads((build/'successor-image-inspect.json').read_text())[0]
assert scan['Metadata']['ImageID'] == inspection['Id']
assert inspection['Config']['Labels']['shopixy.io/source-input-sha256'] == sha(manifest)
assert inspection['Config']['User'] == '1654:1654'
runtime = (build/'successor-runtime.txt').read_text()
assert 'Microsoft.NETCore.App 10.0.15' in runtime and 'Microsoft.AspNetCore.App 10.0.15' in runtime
assemblies = {name:value for value,name in (line.split('  ',1) for line in (build/'successor-assembly-sha256.txt').read_text().splitlines())}
with tarfile.open(candidate/'api.tar') as tar:
    rows = json.load(tar.extractfile('manifest.json')); assert len(rows) == 1
    config_id = 'sha256:'+sha(tar.extractfile(rows[0]['Config']).read())
archive = candidate/'api.tar.gz'
assert not archive.exists(), 'Preserve existing archive'
with (candidate/'api.tar').open('rb') as source,archive.open('xb') as destination:
    with gzip.GzipFile(fileobj=destination,mode='wb',mtime=0) as compressed: shutil.copyfileobj(source,compressed)
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'image':'docker.io/'+image.removeprefix('docker.io/'),
    'runtimeConfigId':config_id,'sourceInputSha256':sha(manifest),'sourceInputsMatchStagedIndex':True,'sourceInputsMatched':len(entries),
    'assemblySha256':[{'filename':name,'sha256':assemblies[name]} for name in ('PointOfSale.API.dll','PointOfSale.Core.dll','PointOfSale.Infrastructure.dll')],
    'archiveSha256':sha(archive.read_bytes()),'securityGatePassed':True,'nugetVulnerablePackages':0,'trivyHighCriticalFindings':0,
    'trivyReportSha256':sha(scan_bytes),'nugetAuditSha256':sha(audit),'runtimeVersion':'10.0.15','uid':1654,
    'productionDeployment':False,'deliveryMethod':'not delivered; local archive only'}
(out/'production-api-candidate.json').write_text(json.dumps(receipt,indent=2)+'\n')
(out/'production-api-source-input-SHA256SUMS.txt').write_bytes(manifest)
print(json.dumps(receipt,indent=2))
