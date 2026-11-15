"""Read-only baseline checks; never rewrite preceding acceptance evidence."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
ORIGINAL = Path('D:/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source')
OUT = Path(__file__).resolve().parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


def main():
    prior = json.loads((ORIGINAL / 'validation/affiliate-production-acceptance/preservation-verification.json').read_text())
    protected = []
    for row in prior['protectedDifferences']:
        actual = digest(ORIGINAL / row['path'])
        assert actual == row['sha256'], 'Protected working difference changed: ' + row['path']
        protected.append({'path': row['path'], 'unchanged': True})
    patch = 'docs/local-work/preserved-working-differences.patch'
    assert digest(ORIGINAL / patch) == prior['preservedPatchSha256']
    source = json.loads((ROOT / 'validation/affiliate-production-remediation/source-index.json').read_text())
    files = source['files']
    # Compare committed blobs, not Windows checkout newline conversion.
    objects = subprocess.run(['git', 'cat-file', '--batch'], cwd=ROOT,
        input=''.join('HEAD:' + name + '\n' for name in files).encode(), capture_output=True, check=True).stdout
    offset = 0
    for name, expected in files.items():
        end = objects.index(b'\n', offset)
        size = int(objects[offset:end].split()[2])
        blob = objects[end + 1:end + 1 + size]
        assert hashlib.sha256(blob).hexdigest() == expected, 'Release-input mismatch: ' + name
        offset = end + 1 + size + 1
    refs = subprocess.check_output(['git', 'ls-remote', 'origin', 'refs/heads/main',
        'refs/heads/affiliate/production-acceptance-20261115'], cwd=ROOT, text=True)
    remote = {line.split()[1]: line.split()[0] for line in refs.splitlines()}
    expected = '8365a92416463136418065f040e8b7f84bcff9fe'
    assert len(remote) == 2 and all(value == expected for value in remote.values())
    reports = ['FINAL-REPORT.md', 'STATUS.md', 'gate-summary.json', 'production-deployment.json',
        'production-identity-verification.json', 'github-delivery.json']
    result = {'verifiedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'baselineCommit': expected, 'implementationCommit': '13bcb260535dc9f7b5ace4d91fbb4e429b3e675f',
        'remoteRefs': remote, 'releaseInputBlobsMatched': len(files),
        'protectedOriginalDifferences': protected, 'all17Unchanged': len(protected) == 17,
        'preservedPatchSha256': digest(ORIGINAL / patch),
        'precedingReports': {name: digest(ROOT / 'validation/affiliate-production-remediation' / name) for name in reports}}
    (OUT / 'baseline-verification.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: value for key, value in result.items() if key not in ('protectedOriginalDifferences', 'precedingReports')}, indent=2))


if __name__ == '__main__':
    main()
