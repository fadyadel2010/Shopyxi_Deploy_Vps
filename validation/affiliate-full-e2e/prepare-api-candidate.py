"""Copy explicit full-build inputs into a new candidate directory; never overwrite prior releases."""
import json
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
target = root.parent / 'production-api-candidate'
complete_resources = sys.argv[1:] == ['--complete-resources']
assert complete_resources or not target.exists(), 'Existing candidate must be inspected, not overwritten'
names = subprocess.check_output(['git', 'ls-files', '-z'], cwd=root).decode().strip('\0').split('\0')
selected = [name for name in names if name.startswith(('PointOfSale.API/', 'PointOfSale.Core/', 'PointOfSale.Infrastructure/'))]
selected += ['global.json', 'deploy/automatic-subdomains/build-successor-image.sh',
    'deploy/automatic-subdomains/successor.Dockerfile', 'deploy/automatic-subdomains/seal-source-inputs.py',
    'theme-authority/shopixy-theme-authority.json']
for name in selected:
    source = root / name
    assert source.is_file()
    destination = target / name
    if complete_resources and destination.exists():
        assert destination.read_bytes() == source.read_bytes(), 'Candidate drift: '+name
        continue
    if complete_resources:
        assert name == 'theme-authority/shopixy-theme-authority.json'
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
print(json.dumps({'candidateDirectory': str(target), 'copiedInputs': len(selected), 'priorEvidenceOverwritten': False}))
