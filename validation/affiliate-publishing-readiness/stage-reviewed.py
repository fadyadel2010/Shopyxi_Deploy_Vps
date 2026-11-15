"""Stage only reviewed readiness sources; never stage another worktree or broad paths."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
assert subprocess.check_output(['git','branch','--show-current'],cwd=root,text=True).strip() == 'affiliate/publishing-readiness-20261115'
names = '''PointOfSale.API/Controllers/AdminAffiliateProductsController.cs
PointOfSale.API/Controllers/AdminProductAffiliateController.cs
PointOfSale.API/Controllers/AffiliatePortalController.cs
PointOfSale.API/Controllers/AffiliatePromotionLinksController.cs
PointOfSale.API/Controllers/PublicAffiliateProductsController.cs
PointOfSale.API/Controllers/StorefrontLandingPageController.cs
PointOfSale.API/Middleware/AffiliateReferralMiddleware.cs
PointOfSale.API/Program.cs
PointOfSale.API/Services/Affiliate/AffiliatePromotionAttributionAuthority.cs
PointOfSale.API/Services/Affiliate/AffiliatePromotionReadinessService.cs
PointOfSale.API/Services/Checkout/CheckoutService.cs
PointOfSale.Core/Interfaces/Affiliate/IAffiliatePromotionReadiness.cs
PointOfSale.Infrastructure/Elasticsearch/Services/AffiliateProductDiscoveryService.cs
PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs
PointOfSale.Tests/Affiliate/AffiliatePublishingReadinessPostgresTests.cs
PointOfSale.Tests/Affiliate/AffiliatePublishingReadinessTests.cs
PointOfSale.Tests/Affiliate/AffiliateReferralJourneyTests.cs
PointOfSale.Web/src/components/ProductAffiliateSettingsCard.jsx
PointOfSale.Web/src/components/ProductAffiliateSettingsCard.test.tsx
PointOfSale.Web/src/components/AffiliatePromotionReadiness.d.ts
PointOfSale.Web/src/components/AffiliatePromotionReadiness.jsx
PointOfSale.Web/src/components/AffiliatePromotionReadiness.test.tsx
PointOfSale.Web/src/i18n/locales/ar.json
PointOfSale.Web/src/i18n/locales/en.json
PointOfSale.Web/src/modules/affiliate/pages/AffiliateProducts.jsx
PointOfSale.Web/src/modules/affiliate/pages/portal/DiscoverTabEnhanced.jsx
PointOfSale.Web/src/modules/affiliate/pages/portal/DiscoverTabEnhanced.readiness.test.tsx
PointOfSale.Web/src/services/adminProductAffiliateApi.d.ts'''.splitlines()
subprocess.run(['git','diff','--check','--',*names],cwd=root,check=True)
subprocess.run(['git','add','--',*names],cwd=root,check=True)
print('Staged '+str(len(names))+' explicitly reviewed readiness source/test files.')
