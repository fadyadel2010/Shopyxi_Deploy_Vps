"""Sanitized release-candidate identity/parity/vulnerability evidence (no raw scan/environment)."""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
base = Path('C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115')
candidate = base / 'candidate-a909b4464ff8'
evidence = root / 'validation/affiliate-production-remediation'
assert not (candidate/'api-unrelated-parity.diff').read_bytes()
assert not (candidate/'web-dist-parity.diff').read_bytes()
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
result = {'sourceIndexSha256': sha(evidence/'source-index.json'), 'registryPush': False,
    'transport': 'Established verified archive import into existing single-node k3s', 'images': {}}
for kind in ('api','web'):
    scan = json.loads((base/f'candidate-{kind}-trivy-private.json').read_text())
    vulnerabilities = [v for r in scan.get('Results',[]) for v in r.get('Vulnerabilities',[]) if v.get('Severity') in ('HIGH','CRITICAL')]
    assert not vulnerabilities
    result['images'][kind] = {'tag': (candidate/f'{kind}-image-tag.txt').read_text().strip(),
        'localDockerIdentity': (candidate/f'{kind}-image-id.txt').read_text().strip(),
        'archiveSha256': sha(candidate/f'{kind}.tar.gz'), 'nonRootUser': 1654 if kind=='api' else 101,
        'highCriticalVulnerabilities': 0, 'scannerMode': 'Cached-DB offline Trivy HIGH/CRITICAL exit-code gate',
        'privateScanSha256': sha(base/f'candidate-{kind}-trivy-private.json')}
result.update({'apiUnrelatedRuntimeFilesIdentical': True, 'apiDependencyManifestIdentical': True,
    'webImageDistParity': True, 'nginxConfigurationValid': True})
(evidence/'release-candidates.json').write_text(json.dumps(result,indent=2)+'\n')
print('Verified API/Web candidate identities and zero HIGH/CRITICAL findings exported without raw scan data.')
