"""Verify the 17 pre-existing differences/absences without changing their content."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
rows = json.loads((root / 'docs/local-work/source-versions.json').read_text())
checks = []
for row in rows:
    path = root / row['path']
    actual = hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None
    assert actual == row['workingSha256'], 'Protected working state changed: ' + row['path']
    checks.append({'path': row['path'], 'preserved': True, 'sha256': actual})
for row in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text()):
    actual = hashlib.sha256((root / row['repositoryPath']).read_bytes()).hexdigest()
    assert actual == row['originalLocalSha256']
    checks.append({'path': row['repositoryPath'], 'preserved': True, 'sha256': actual})
assert len(checks) == 17
patch = 'docs/local-work/preserved-working-differences.patch'
data = (root / patch).read_bytes()
assert hashlib.sha256(data).hexdigest() == 'f65a1fb038b4285e4ec7a7e41ee0271412933f70a4cd5b8816d8e67f7dd20468'
assert subprocess.check_output(['git', 'show', 'HEAD:' + patch], cwd=root) == data
result = {'protectedDifferences': checks, 'all17Unchanged': True,
    'preservedPatchMatchesHead': True, 'preservedPatchSha256': hashlib.sha256(data).hexdigest()}
(Path(__file__).resolve().parent / 'preservation-verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8', newline='\n')
print('All 17 original differences/absences and the preserved patch are unchanged.')
