"""Sync only explicit Affiliate remediation changes into the isolated accepted baseline.
Never copy the 17 unrelated original working differences into builds.
"""
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
stage = Path('C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115/source')
protected = {r['path'] for r in json.loads((root / 'docs/local-work/source-versions.json').read_text())}
protected |= {r['repositoryPath'] for r in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text())}
changed = subprocess.check_output(['git', 'diff', '--name-only', 'HEAD'], cwd=root).decode().splitlines()
new = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=root).decode().splitlines()
paths = [p for p in changed + new if p not in protected and p.startswith(('PointOfSale.Core/', 'PointOfSale.Infrastructure/', 'PointOfSale.API/', 'PointOfSale.Tests/', 'PointOfSale.Web/'))]
for name in paths:
    target = stage / name
    assert (root / name).is_file(), 'Review deletion explicitly before changing the isolated build'
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(root / name, target)
print('Copied', len(paths), 'task-owned source files; protected versions excluded.')
for name in ('AffiliateRemediationTests.csproj', 'inside-isolated-tests.sh'):
    target = stage / 'validation/affiliate-production-remediation' / name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(root / 'validation/affiliate-production-remediation' / name, target)
for name in ('05-manual-payout-return-runtime-grant.sql', '06-manual-payout-return-runtime-revoke.sql', '07-manual-payout-return-schema-version.sql'):
    target = stage / 'deploy/affiliate' / name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(root / 'deploy/affiliate' / name, target)
