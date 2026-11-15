"""Scan exact focused staged bytes. Never print credentials or scanner matches."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
out = Path(__file__).resolve().parent
def git(*args):
    return subprocess.check_output(['git', *args], cwd=root)
paths = git('diff', '--cached', '--name-only', '-z').decode().strip('\0').split('\0')
assert paths and all(p.startswith(('validation/affiliate-production-acceptance/', 'deploy/affiliate/')) or p in (
    'PointOfSale.Web/src/modules/affiliate/pages/portal/ProfileTab.jsx',
    'PointOfSale.Web/src/modules/affiliate/pages/portal/ProfileTab.test.tsx') for p in paths)
identity = json.loads((Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-acceptance-private/qa-identity.json').read_text())
protected = [identity['email'].encode(), identity['password'].encode()]
rows = {}
for p in paths:
    assert not any(s in p.lower() for s in ('__pycache__', '.playwright-cli', 'browser-state', 'qa-identity'))
    data = git('show', ':' + p)
    assert not any(value.lower() in data.lower() for value in protected), 'Private QA value in staged bytes; withheld'
    assert not data.startswith(b'\xff\xfe'), 'Normalize own UTF-16 evidence before scanning'
    rows[p] = {'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
# Verify every compiled Web input against the prospective commit, not the dirty
# unrelated Windows working versions. One git cat-file process avoids 2000 spawns.
source = json.loads((out / 'frontend-source-index.json').read_text())['files']
stage = {line.split('\t')[1]: line.split()[1] for line in git('ls-files', '--stage', 'PointOfSale.Web').decode().splitlines()}
assert set(source) == set(stage)
objects = [stage[p] for p in source]
batch = subprocess.run(['git', 'cat-file', '--batch'], input=('\n'.join(objects) + '\n').encode(), capture_output=True, check=True, cwd=root).stdout
offset = 0
for p in source:
    end = batch.index(b'\n', offset)
    length = int(batch[offset:end].split()[2])
    blob = batch[end + 1:end + 1 + length]
    assert hashlib.sha256(blob).hexdigest() == source[p], 'Staged build source differs: ' + p
    offset = end + length + 2
wslroot = '/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source'
cmd = ['wsl', '-d', 'Shopixy-APP1-Ubuntu-Docker', '--cd', wslroot, '--exec', 'env',
    'GIT_CONFIG_COUNT=1', 'GIT_CONFIG_KEY_0=safe.directory', 'GIT_CONFIG_VALUE_0=' + wslroot,
    '/opt/shopixy-toolchain/bin/gitleaks', 'git', '--pre-commit', '--staged', '--redact=100',
    '--no-banner', '--no-color', '--ignore-gitleaks-allow', '--log-level', 'error', '--report-format', 'json', '--report-path', '-', wslroot]
p = subprocess.run(cmd, capture_output=True, timeout=180)
assert p.returncode in (0, 1), 'Gitleaks failed; no raw scanner output published'
findings = json.loads(p.stdout or b'[]')
safe = []
for f in findings:
    rel = f['File'].removeprefix(wslroot + '/').removeprefix('./')
    reason = None
    if rel == 'validation/affiliate-production-acceptance/frontend-source-index.json' and f['RuleID'] == 'generic-api-key':
        line = git('show', ':' + rel).decode().splitlines()[f['StartLine'] - 1]
        match = re.fullmatch(r'\s*"([^"]+)": "([0-9a-f]{64})",?', line)
        if match and source.get(match[1]) == match[2]:
            reason = 'Integrity SHA-256 of the exact compiled Web input; independently verified against staged blob above'
    safe.append({'path': rel, 'line': f['StartLine'], 'rule': f['RuleID'], 'matchOmitted': True,
        'review': reason or 'BLOCKED: requires review'})
blocked = [f for f in safe if f['review'].startswith('BLOCKED')]
result = {'scannedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'mode': 'Actual Git staged diff; existing Gitleaks default rules, 100% redaction, no allow-comment bypass',
    'stagedFiles': len(rows), 'compiledWebInputFilesMatchedToIndex': len(source),
    'exactPrivateQaValuesAbsent': True, 'rawFindings': len(safe), 'findings': safe,
    'reviewedIntegrityDigests': len(safe) - len(blocked), 'unreviewedOrRealSecretFindings': len(blocked),
    'passed': not blocked, 'sourceIndexSha256': hashlib.sha256((out / 'frontend-source-index.json').read_bytes()).hexdigest()}
if '--check-only' not in sys.argv[1:]:
    (out / 'staged-secret-scan.json').write_text(json.dumps(result, indent=2), encoding='utf-8', newline='\n')
print(json.dumps({k:v for k,v in result.items() if k!='findings'}, indent=2))
if blocked: print(json.dumps(blocked, indent=2))
assert result['passed'], 'Review scanner metadata before commit; no match values printed'
