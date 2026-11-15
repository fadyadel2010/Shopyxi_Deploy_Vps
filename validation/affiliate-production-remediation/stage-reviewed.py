"""Stage only task-owned Affiliate remediation paths; never the preserved working differences."""
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
protected = {r['path'] for r in json.loads((root / 'docs/local-work/source-versions.json').read_text())}
protected |= {r['repositoryPath'] for r in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text())}
changed = subprocess.check_output(['git', 'diff', '--name-only', 'HEAD'], cwd=root).decode().splitlines()
new = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=root).decode().splitlines()
prefixes = ('PointOfSale.Core/Entities/Affiliate/', 'PointOfSale.Core/Events/Affiliate/', 'PointOfSale.Core/Interfaces/Affiliate/',
    'PointOfSale.Infrastructure/Services/Affiliate/', 'PointOfSale.Infrastructure/Consumers/Affiliate/',
    'PointOfSale.Infrastructure/Data/Configurations/Affiliate/', 'PointOfSale.Infrastructure/Migrations/',
    'PointOfSale.Tests/Affiliate/', 'PointOfSale.Web/src/modules/affiliate/', 'deploy/affiliate/')
exact = {'PointOfSale.API/Controllers/AdminAffiliateController.cs', 'PointOfSale.API/Controllers/AffiliatePortalController.cs',
    'validation/affiliate-production-acceptance/FINAL-REPORT.md',
    'PointOfSale.API/Controllers/AffiliateFinancialErrorContract.cs', 'PointOfSale.API/Controllers/AffiliateManualPayoutReturnController.cs',
    'PointOfSale.API/Security/PosEndpointPermissionCatalog.cs', 'PointOfSale.Infrastructure/Accounting/FinancialPostingService.cs',
    'PointOfSale.Infrastructure/Data/ApplicationDbContext.cs', 'PointOfSale.Infrastructure/EventBus/MassTransitEventPublisher.cs',
    'PointOfSale.Infrastructure/MongoDB/Documents/Affiliate/AffiliateDocuments.cs',
    'PointOfSale.Infrastructure/Notifications/OrderCustomerCommunicationAuthority.cs',
    'PointOfSale.Infrastructure/WhatsApp/Services/WhatsAppDispatchAuthority.cs',
    'PointOfSale.Web/src/services/adminAffiliateApi.js', 'PointOfSale.Web/src/services/adminAffiliateApi.d.ts',
    'PointOfSale.Web/src/i18n/locales/en.json', 'PointOfSale.Web/src/i18n/locales/ar.json'}
paths = sorted({p for p in changed + new if p not in protected and (p.startswith(prefixes) or p in exact)})
evidence = root / 'validation/affiliate-production-remediation'
paths += [p.relative_to(root).as_posix() for p in sorted(evidence.iterdir()) if p.is_file()]
assert not any('__pycache__' in p or p.endswith(('.tar.gz', '.trx')) or 'private' in Path(p).name for p in paths)
assert all((root / p).is_file() for p in paths), 'Review deletions explicitly'
assert not protected.intersection(paths)
subprocess.run(['git', 'add', '-f', '--', *paths], cwd=root, check=True)
print('Staged', len(paths), 'explicit task-owned source/deploy/evidence files; protected versions excluded.')
