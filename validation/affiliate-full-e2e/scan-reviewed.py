"""Scan exact staged bytes and compare private values without exporting any secret match."""
import datetime
import json
import os
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
git = lambda *args: subprocess.check_output(['git', *args], cwd=root)
names = git('diff', '--cached', '--name-only', '-z').decode().strip('\0').split('\0')
assert names and all(name.startswith('validation/affiliate-full-e2e/') or name in (
    'PointOfSale.Infrastructure/Accounting/ManualCodSettlementAuthority.cs',
    'PointOfSale.Tests/Affiliate/AffiliateJourneyCashboxAuthorityTests.cs') for name in names)
private_values = []
def collect(value, key=''):
    if isinstance(value, dict):
        for k, v in value.items(): collect(v, k)
    elif isinstance(value, list):
        for item in value: collect(item, key)
    elif isinstance(value, str) and len(value) >= 8 and re.search('password|secret|token|email|connectionstring', key, re.I):
        private_values.append(value.encode())
for path in root.parent.glob('local-*-private.json'):
    collect(json.loads(path.read_text()))
for name in names:
    assert not any(word in name.lower() for word in ('private', '__pycache__', '.playwright', 'browser-state'))
    blob = git('show', ':' + name)
    assert not any(value in blob for value in private_values), 'Private value found; match withheld'
wslroot = '/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-full-e2e-20261115/source'
result = subprocess.run(['wsl', '-d', 'Shopixy-APP1-Ubuntu-Docker', '--cd', wslroot, '--exec', 'env',
    'GIT_CONFIG_COUNT=1', 'GIT_CONFIG_KEY_0=safe.directory', 'GIT_CONFIG_VALUE_0='+wslroot,
    '/opt/shopixy-toolchain/bin/gitleaks', 'git', '--pre-commit', '--staged', '--redact=100', '--no-banner', '--no-color',
    '--ignore-gitleaks-allow', '--log-level', 'error', '--report-format', 'json', '--report-path', '-', wslroot],
    capture_output=True, timeout=240)
assert result.returncode in (0, 1), 'Scanner failed; raw output withheld'
findings = json.loads(result.stdout or b'[]')
metadata = [{'path': item['File'].removeprefix(wslroot+'/').removeprefix('./'),
    'line': item['StartLine'], 'rule': item['RuleID'], 'matchOmitted': True} for item in findings]
receipt = {'verifiedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'stagedFiles': len(names), 'privateValuesChecked': len(private_values), 'exactPrivateValuesAbsent': True,
    'rawFindings': len(findings), 'findings': metadata, 'passed': not findings}
(root / 'validation/affiliate-full-e2e/staged-secret-scan.json').write_text(json.dumps(receipt, indent=2)+'\n')
print(json.dumps(receipt, indent=2))
assert receipt['passed'], 'Review redacted finding metadata before proceeding'
