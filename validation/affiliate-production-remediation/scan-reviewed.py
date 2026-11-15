"""Scan exact staged bytes and prove the prospective index matches every release input."""
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
git = lambda *args: subprocess.check_output(['git', *args], cwd=root)
paths = git('diff', '--cached', '--name-only', '-z').decode().strip('\0').split('\0')
protected_paths = {r['path'] for r in json.loads((root / 'docs/local-work/source-versions.json').read_text())}
protected_paths |= {r['repositoryPath'] for r in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text())}
assert paths and not protected_paths.intersection(paths)
identity = json.loads((Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-acceptance-private/qa-identity.json').read_text())
private_values = [identity['email'].encode(), identity['password'].encode()]
for name in paths:
    assert not any(part in name.lower() for part in ('__pycache__', '.playwright-cli', 'browser-state', 'qa-identity'))
    blob = git('show', ':' + name)
    assert not any(value.lower() in blob.lower() for value in private_values), 'Private QA value detected; withheld'
source = json.loads((out / 'source-index.json').read_text())['files']
index = {line.split('\t', 1)[1]: line.split()[1] for line in git('ls-files', '--stage').decode().splitlines()}
assert all(p in index for p in source)
objects = [index[p] for p in source]
data = subprocess.run(['git', 'cat-file', '--batch'], cwd=root, input=('\n'.join(objects)+'\n').encode(), capture_output=True, check=True).stdout
offset = 0
for name in source:
    end = data.index(b'\n', offset); length = int(data[offset:end].split()[2]); blob = data[end+1:end+1+length]
    assert hashlib.sha256(blob).hexdigest() == source[name], 'Prospective commit differs from release input: ' + name
    offset = end + 1 + length + 1
wslroot = '/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source'
command = ['wsl', '-d', 'Shopixy-APP1-Ubuntu-Docker', '--cd', wslroot, '--exec', 'env',
    'GIT_CONFIG_COUNT=1', 'GIT_CONFIG_KEY_0=safe.directory', 'GIT_CONFIG_VALUE_0='+wslroot,
    '/opt/shopixy-toolchain/bin/gitleaks', 'git', '--pre-commit', '--staged', '--redact=100', '--no-banner', '--no-color',
    '--ignore-gitleaks-allow', '--log-level', 'error', '--report-format', 'json', '--report-path', '-', wslroot]
scan = subprocess.run(command, capture_output=True, timeout=240)
assert scan.returncode in (0,1), 'Scanner execution failed; raw output withheld'
findings = json.loads(scan.stdout or b'[]'); reviewed=[]; blocked=[]
for item in findings:
    name = item['File'].removeprefix(wslroot+'/').removeprefix('./')
    reason = None
    if name == 'validation/affiliate-production-remediation/source-index.json' and item['RuleID'] == 'generic-api-key':
        line = git('show', ':'+name).decode().splitlines()[item['StartLine']-1]
        match = re.fullmatch(r'\s*"([^"]+)": "([0-9a-f]{64})",?', line)
        if match and source.get(match[1]) == match[2]: reason='Integrity SHA-256 independently matched to prospective release input'
    if name == 'validation/affiliate-production-remediation/deploy-phase-two.py' and item['RuleID'] == 'generic-api-key':
        line = git('show', ':'+name).decode().splitlines()[item['StartLine']-1]
        candidate = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-remediation-20261115/candidate-a909b4464ff8'
        checksum = re.fullmatch(r"API_SHA = '([0-9a-f]{64})'", line)
        tag = re.fullmatch(r"API_TAG = '(docker\.io/shopixy/api:affiliate-return-20261115-[0-9a-f]{12})'", line)
        if checksum and hashlib.sha256((candidate/'api.tar.gz').read_bytes()).hexdigest() == checksum[1]:
            reason='Public-format archive integrity SHA-256 independently verified against packaged API archive'
        if tag and tag[1] == 'docker.io/' + (candidate/'api-image-tag.txt').read_text().strip():
            reason='Non-secret OCI image reference independently verified against packaged image tag'
    if name == 'validation/affiliate-production-remediation/release-candidates.json' and item['RuleID'] == 'generic-api-key':
        line = git('show', ':'+name).decode().splitlines()[item['StartLine']-1]
        match = re.fullmatch(r'\s*"tag": "(shopixy/(api|web):affiliate-return-20261115-[0-9a-f]{12})",?', line)
        candidate = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-remediation-20261115/candidate-a909b4464ff8'
        if match and match[1] == (candidate/f'{match[2]}-image-tag.txt').read_text().strip():
            reason='Non-secret OCI image reference independently verified against packaged image tag'
    if name in ('validation/affiliate-production-remediation/production-deployment.json',
                'validation/affiliate-production-remediation/production-release-attempts.json') and item['RuleID'] == 'generic-api-key':
        line = git('show', ':'+name).decode().splitlines()[item['StartLine']-1]
        match = re.fullmatch(r'\s*"(api|web)": "([0-9a-f]{64})",?', line)
        candidate = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-remediation-20261115/candidate-a909b4464ff8'
        if match and hashlib.sha256((candidate/f'{match[1]}.tar.gz').read_bytes()).hexdigest() == match[2]:
            reason='Deployment archive integrity SHA-256 independently verified against packaged release archive'
    metadata={'path':name,'line':item['StartLine'],'rule':item['RuleID'],'matchOmitted':True,'review':reason or 'Requires explicit review'}
    (reviewed if reason else blocked).append(metadata)
result={'scannedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stagedFiles':len(paths),
    'compiledSourceFilesMatchedToIndex':len(source),'exactPrivateQaValuesAbsent':True,'rawFindings':len(findings),
    'reviewedIntegrityDigests':len(reviewed),'unreviewedOrRealSecretFindings':len(blocked),'passed':not blocked,
    'sourceIndexSha256':hashlib.sha256((out/'source-index.json').read_bytes()).hexdigest()}
if '--check-only' not in sys.argv[1:]: (out/'staged-secret-scan.json').write_text(json.dumps({**result,'findings':reviewed+blocked},indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(result,indent=2))
if blocked: print(json.dumps(blocked,indent=2))
assert result['passed'], 'Review scanner metadata before commit; no match values printed'
