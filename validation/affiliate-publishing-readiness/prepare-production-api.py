"""Seal explicit API build inputs in a new directory; no runtime or Production configuration."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
target = root.parent/'publishing-readiness-production-api'
assert not target.exists(), 'Do not overwrite a candidate or previous release evidence'
names = subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=root).decode().strip('\0').split('\0')
selected = sorted({name for name in names if name.startswith(('PointOfSale.API/','PointOfSale.Core/','PointOfSale.Infrastructure/'))})
selected += ['global.json','deploy/automatic-subdomains/build-successor-image.sh',
    'deploy/automatic-subdomains/successor.Dockerfile','deploy/automatic-subdomains/seal-source-inputs.py',
    'theme-authority/shopixy-theme-authority.json']
manifest = []
for name in selected:
    source = root/name
    assert source.is_file()
    destination = target/name
    destination.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(source,destination)
    manifest.append({'path':name,'sha256':hashlib.sha256(source.read_bytes()).hexdigest()})
(target/'input-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps({'copiedInputs':len(selected),'priorEvidenceOverwritten':False,'productionMutations':False}))
