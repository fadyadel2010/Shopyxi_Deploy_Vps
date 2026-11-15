"""Stage an explicit evidence allowlist, never private runtime files or prior reports."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
evidence = 'validation/affiliate-full-e2e/'
names = '''.gitattributes
FINAL-REPORT.md
baseline-verification.json
production-runtime-current.json
browser-owner-isolation.json
browser-affiliate-isolation.json
browser-customer-isolation.json
isolation-readiness.json
checkout-submitted.json
fulfillment-confirm.json
fulfillment-pack.json
fulfillment-dispatch.json
fulfillment-delivered.json
fulfillment-completed.json
collection-confirmed.json
commission-approved.json
withdrawal-requested.json
withdrawal-approved.json
cod-remittance-blocker.json
cod-remittance-confirmed.json
cod-remittance-replayed.json
payout-cashbox-funded.json
manual-payout-confirmed.json
payout-return-failures.json
manual-payout-return-confirmed.json
payout-return-replayed.json
second-withdrawal-requested.json
second-withdrawal-cancelled.json
main-cashbox-restored.json
order-refunded.json
final-ui-reconciliation.json
final-reconciliation-summary.json
state-before-checkout.json
state-completed.json
state-payout-paid.json
state-payout-returned.json
state-new-approval-required.json
state-second-withdrawal-cancelled.json
state-final-reconciliation-v2.json
affiliate-final-reconciliation.png
owner-affiliate-final-reconciliation.png
results/journey-before.trx
results/journey-after.trx
results/journey-standard-owned-fixture.trx
AffiliateJourneyTests.csproj
inside-focused.sh
inside-standard-tests.sh
run-focused.sh
verify-final.py
verify-baseline.py
verify-live-runtime.py
prepare-api-candidate.py
build-production-api.sh
deploy-api-only.py
recover-api-import-alias.py
run-api-alias-recovery.py
recover-normalized-import-alias.py
run-normalized-alias-recovery.py
upload-and-deploy-api.py
verify-production-boundary.py
collect-api-candidate.py
collect-gates.py
collect-security-gate.py
deliver-github.py
stage-reviewed.py
scan-reviewed.py'''.splitlines()
paths = [evidence + name for name in names]
for name in ('staged-secret-scan.json', 'production-api-candidate.json',
    'gate-summary.json', 'production-security-gate.json',
    'production-api-source-input-SHA256SUMS.txt', 'production-deployment.json',
    'production-release-attempts.json', 'production-alias-rollback.json', 'production-rollout-diagnosis.json',
    'production-boundary-before-api-release.json', 'production-boundary-after-api-release.json',
    'production-runtime-after-api-release.json', 'github-delivery.json'):
    if (root / evidence / name).is_file(): paths.append(evidence + name)
paths += ['PointOfSale.Infrastructure/Accounting/ManualCodSettlementAuthority.cs',
    'PointOfSale.Tests/Affiliate/AffiliateJourneyCashboxAuthorityTests.cs']
assert all((root / name).is_file() for name in paths)
assert all('private' not in Path(name).name.lower() for name in paths)
subprocess.run(['git', '-c', 'core.safecrlf=false', 'add', '-f', '--', *paths], cwd=root, check=True)
subprocess.run(['git', '-c', 'core.safecrlf=false', 'add', '--renormalize', '--', *paths], cwd=root, check=True)
print(f'Staged {len(paths)} explicit task-owned files; no recursive ignored-directory staging.')
