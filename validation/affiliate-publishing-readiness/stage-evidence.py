"""Stage an explicit sanitized evidence family only; exclude raw logs, sessions and old diagnostics."""
from pathlib import Path
import subprocess

root=Path(__file__).resolve().parents[2];out=Path(__file__).parent
scripts='''browser-run.py build-local.py build-web.sh restore-web.sh install-local.py run-postgres.sh run-referral-dependency.sh
capture-safety.py capture-click-safety.py run-DE.py final-candidate-browser.py final-focus.py
affiliate-blocked.js affiliate-restored.js referral-blocked.js browser-errors.js
catalog-focus-blocked.js catalog-focus-restored.js prepare-production-api.py build-production-api.sh
collect-production-api.py package-production-web.py collect-gates.py prepare-release-input.py
deploy-api-web.py upload-and-deploy.py verify-runtime.py verify-production-boundary.py verify-protected.py
production-context.js production-readonly.js production-browser.py stage-reviewed.py scan-reviewed.py stage-evidence.py
inspect-release.py inspect-image-admission.py deploy-api-web-retry.py retry-release.py verify-source-parity.py verify-git-delivery.py'''.split()
receipts='''FINAL-REPORT.md gate-summary.json production-api-candidate.json production-web-candidate.json
production-api-source-input-SHA256SUMS.txt release-input.json production-runtime-before.json production-runtime-pre-deploy.json
production-runtime-after-release.json production-boundary-before.json production-boundary-after.json production-deployment.json
production-readonly.json staged-secret-scan.json protected-worktree-verification.json
production-deployment-failed.json production-runtime-after-rollback.json release-retry-input.json image-admission-diagnosis.json
source-parity-index.json source-parity-commit.json git-delivery.json
history-safety-review-before.json history-safety-review-after.json click-safety-review-before.json click-safety-review-after.json
browser-errors.json final-candidate-browser.json catalog-focus-blocked-final.json catalog-focus-restored-final.json'''.split()
names=set(scripts+receipts)
for path in out.iterdir():
    if path.is_file() and path.suffix in ('.js','.json','.png') and path.name.startswith(('scenario-','scenarios-','final-candidate-','final-focus-','status-','product-')):
        names.add(path.name)
selected=[str((out/name).relative_to(root)).replace('\\','/') for name in sorted(names) if (out/name).is_file()]
assert selected and not any(any(word in name for word in ('private','__pycache__','.log','.trx')) for name in selected)
assert all('inspect-' not in name or Path(name).name in ('inspect-release.py','inspect-image-admission.py') for name in selected)
subprocess.run(['git','add','-f','--',*selected],cwd=root,check=True)
print('Staged '+str(len(selected))+' explicitly scoped sanitized readiness evidence/helper files.')
