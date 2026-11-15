set -euo pipefail
cd /work
test ! -e package-lock.json
cp /src/PointOfSale.Web/{package.json,package-lock.json} .
npm ci --ignore-scripts --no-audit --no-fund --cache /cache
cmp package-lock.json /src/PointOfSale.Web/package-lock.json
