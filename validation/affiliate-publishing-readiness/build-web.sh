set -euo pipefail
mkdir -p /work/PointOfSale.Web
cp /src/PointOfSale.Web/{package.json,package-lock.json,tsconfig.json,tsconfig.node.json,vite.config.ts,vitest.config.ts,tailwind.config.js,postcss.config.js,index.html} /work/PointOfSale.Web/
cp -r /src/PointOfSale.Web/{src,scripts} /work/PointOfSale.Web/
cp -r /src/{PointOfSale.Storefront,PointOfSale.CommerceHub,theme-authority} /work/
cmp /src/PointOfSale.Web/package-lock.json /dependency-lock.json
cp -a /deps/node_modules /work/PointOfSale.Web/node_modules
ln -s /work/PointOfSale.Web/node_modules /work/node_modules
cd /work/PointOfSale.Web
node --version
node node_modules/vitest/vitest.mjs run src/components/AffiliatePromotionReadiness.test.tsx src/modules/affiliate/pages/portal/DiscoverTabEnhanced.readiness.test.tsx --reporter=json --outputFile=/out/frontend-tests.json
node node_modules/vitest/vitest.mjs run src/components/ProductAffiliateSettingsCard.test.tsx -t 'publishing readiness' --reporter=json --outputFile=/out/frontend-policy-readiness-tests.json
node node_modules/typescript/bin/tsc --noEmit
node node_modules/vite/bin/vite.js build
cp -r dist /out/
