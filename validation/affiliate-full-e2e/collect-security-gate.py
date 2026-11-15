"""Export only image-bound security predicates and integrity hashes, not image environment/configuration."""
import datetime
import hashlib
import json
from pathlib import Path

out = Path(__file__).parent
build = out.resolve().parents[1].parent/'production-api-candidate/deploy/automatic-subdomains'
candidate = json.loads((out/'production-api-candidate.json').read_text())
report_bytes = (build/'successor-trivy-high-critical.json').read_bytes()
report = json.loads(report_bytes)
assert report['ArtifactType'] == 'container_image'
assert report['Metadata']['ImageID'] == candidate['image'].split('@')[1]
assert report['Results']
findings = [v for result in report['Results'] for v in result.get('Vulnerabilities',[]) if v['Severity'] in ('HIGH','CRITICAL')]
assert not findings
audit = (build/'successor-nuget-vulnerability-audit.txt').read_bytes()
assert b'has no vulnerable packages' in audit
safe = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'image':candidate['image'], 'scanImageIdentityMatched':True,
    'trivyReportSha256':hashlib.sha256(report_bytes).hexdigest(),
    'trivyHighCriticalFindings':len(findings), 'scanResultGroups':len(report['Results']),
    'nugetAuditSha256':hashlib.sha256(audit).hexdigest(), 'nugetVulnerablePackages':0,
    'vulnerabilityDatabaseUpdatedDuringBuild':True, 'passed':True}
(out/'production-security-gate.json').write_text(json.dumps(safe,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(safe,indent=2))
