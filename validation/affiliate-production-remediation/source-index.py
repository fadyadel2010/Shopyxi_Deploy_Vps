"""Index the isolated release source, not unrelated original working versions."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
stage = Path('C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115/source')
evidence = root / 'validation/affiliate-production-remediation'
protected = {r['path'] for r in json.loads((root / 'docs/local-work/source-versions.json').read_text())}
protected |= {r['repositoryPath'] for r in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text())}
prefixes = ('PointOfSale.API/', 'PointOfSale.Core/', 'PointOfSale.Infrastructure/', 'PointOfSale.Web/', 'PointOfSale.Storefront/', '.config/')
tracked = subprocess.check_output(['git', 'ls-files'], cwd=stage).decode().splitlines()
untracked = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=stage).decode().splitlines()
paths = sorted({p for p in tracked + untracked if p.startswith(prefixes)})
sha = lambda data: hashlib.sha256(data).hexdigest()
for name in protected:
    result = subprocess.run(['git', 'show', 'HEAD:' + name], cwd=stage, capture_output=True)
    if result.returncode == 0:
        assert (stage / name).read_bytes() == result.stdout, 'Unrelated working version leaked into build: ' + name
changes = subprocess.check_output(['git', 'diff', '--name-only', 'HEAD'], cwd=root).decode().splitlines()
new = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=root).decode().splitlines()
for name in changes + new:
    if name.startswith(prefixes) and name not in protected:
        assert (stage / name).read_bytes() == (root / name).read_bytes(), 'Release source drift: ' + name
manifest = {'baselineCommit': 'bd25235', 'protectedWorkingVersionsExcluded': True,
    'files': {name: sha((stage / name).read_bytes()) for name in paths}}
(evidence / 'source-index.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('Indexed', len(paths), 'isolated source files; current fixes match and protected working versions are excluded.')
