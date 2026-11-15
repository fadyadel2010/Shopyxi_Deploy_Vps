"""Normalize own text receipts and capture allowlisted release metadata, never private financial rows."""
import json
import os
from pathlib import Path
import subprocess

out = Path(__file__).resolve().parent
for name in ('runtime-before.json', 'grant-regression.log'):
    path = out / name
    data = path.read_bytes()
    if data.startswith(b'\xff\xfe'):
        path.write_text(data.decode('utf-16'), encoding='utf-8', newline='\n')
private = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-20261115'
scan = json.loads((private / 'frontend-trivy-private.json').read_text())
vulns = [v for r in scan.get('Results', []) for v in r.get('Vulnerabilities', [])
    if v.get('Severity') in ('HIGH', 'CRITICAL')]
assert not vulns
(out / 'frontend-vulnerability-summary.json').write_text(json.dumps({
    'scanner': 'Existing Trivy; offline cached vulnerability database', 'createdAt': scan.get('CreatedAt'),
    'highCriticalFindings': len(vulns), 'passed': True,
    'limitation': 'No online database update; not a claim of current advisory completeness.'}, indent=2), encoding='utf-8')
script = (out / 'runtime-readonly.py').read_text()
p = subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe', '-o', 'BatchMode=yes', 'shopixy-vps', 'python3', '-'],
    input=script, capture_output=True, text=True, timeout=150)
assert p.returncode == 0, 'Read-only runtime check failed; raw output withheld'
runtime = json.loads(p.stdout)
(out / 'runtime-after.json').write_text(json.dumps(runtime, indent=2), encoding='utf-8')
print('Own text evidence normalized, offline scan summarized, final runtime identity captured.')
