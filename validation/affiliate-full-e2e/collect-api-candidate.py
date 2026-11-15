"""Admit the completed full build only after security, staged-source and immutable image parity gates."""
import datetime
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
candidate = root.parent / 'production-api-candidate'
build = candidate / 'deploy/automatic-subdomains'
out = Path(__file__).parent
sha = lambda value: hashlib.sha256(value).hexdigest()
git = lambda *args: subprocess.check_output(['git', *args], cwd=root)
manifest = (build/'source-input-SHA256SUMS.txt').read_bytes()
entries = [line.split('  ',1) for line in manifest.decode().splitlines()]
assert entries
batch = subprocess.run(['git','cat-file','--batch'], cwd=root,
    input=''.join(':'+name+'\n' for _,name in entries).encode(),capture_output=True,check=True).stdout
offset = 0
for expected, name in entries:
    assert sha((candidate/name).read_bytes()) == expected
    end = batch.index(b'\n',offset)
    length = int(batch[offset:end].split()[2])
    blob = batch[end+1:end+1+length]
    assert sha(blob) == expected, 'Staged source differs from build input: '+name
    offset = end+1+length+1
theme = 'theme-authority/shopixy-theme-authority.json'
assert (candidate/theme).read_bytes() == git('show', ':'+theme)
audit = (build/'successor-nuget-vulnerability-audit.txt').read_text()
assert 'has no vulnerable packages' in audit
trivy = json.loads((build/'successor-trivy-high-critical.json').read_text())
vulnerabilities = [v for item in trivy.get('Results',[]) for v in item.get('Vulnerabilities',[]) if v['Severity'] in ('HIGH','CRITICAL')]
assert not vulnerabilities
image = (build/'successor-image-reference.txt').read_text().strip()
inspected = json.loads((build/'successor-image-inspect.json').read_text())[0]
labels = inspected['Config']['Labels']
assert labels['shopixy.io/source-input-sha256'] == sha(manifest)
assert inspected['Config']['User'] == '1654:1654'
runtime = (build/'successor-runtime.txt').read_text()
assert 'Microsoft.AspNetCore.App 10.0.15' in runtime and 'Microsoft.NETCore.App 10.0.15' in runtime
assembly = dict((name,value) for value,name in (line.split('  ',1) for line in (build/'successor-assembly-sha256.txt').read_text().splitlines()))
archive = candidate/'api.tar.gz'
assert not archive.exists(), 'Existing archive must be inspected, not overwritten'
with (candidate/'api.tar').open('rb') as source, archive.open('xb') as destination:
    with gzip.GzipFile(fileobj=destination,mode='wb',mtime=0) as compressed:
        shutil.copyfileobj(source,compressed)
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'image':'docker.io/'+image.removeprefix('docker.io/'),
    'runtimeConfigId':inspected.get('Config',{}).get('Image') or inspected['Id'],
    'sourceInputSha256':sha(manifest), 'sourceInputsMatchStagedIndex':True, 'sourceInputsMatched':len(entries),
    'additionalEmbeddedThemeResourceSha256':sha((candidate/theme).read_bytes()),
    'assemblySha256':{name:assembly[name] for name in ('PointOfSale.API.dll','PointOfSale.Infrastructure.dll')},
    'archiveSha256':sha(archive.read_bytes()), 'securityGatePassed':True, 'nugetVulnerablePackages':0,
    'trivyHighCriticalFindings':0, 'runtimeVersion':'10.0.15', 'uid':1654,
    'baseImage':'docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e',
    'deploymentScriptSha256':sha((out/'deploy-api-only.py').read_bytes())}
# Docker's image-inspect Id can be an index in this toolchain; the OCI config
# digest is obtained from the saved image's selected manifest, not guessed.
layout = subprocess.check_output(['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--exec','env',
    'DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock','DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker',
    '/opt/shopixy-toolchain/bin/docker','image','inspect',image,'--format','{{json .}}'])
observed = json.loads(layout)
assert observed['Id'] == inspected['Id']
# Container Config.Image is not a content config identity; read the OCI archive.
import tarfile
with tarfile.open(candidate/'api.tar') as tar:
    manifest_rows = json.load(tar.extractfile('manifest.json'))
    assert len(manifest_rows) == 1
    config_path = manifest_rows[0]['Config']
    receipt['runtimeConfigId'] = 'sha256:'+sha(tar.extractfile(config_path).read())
(candidate/'candidate.json').write_text(json.dumps(receipt,indent=2)+'\n')
(out/'production-api-candidate.json').write_text(json.dumps(receipt,indent=2)+'\n')
(out/'production-api-source-input-SHA256SUMS.txt').write_bytes(manifest)
print(json.dumps(receipt,indent=2))
