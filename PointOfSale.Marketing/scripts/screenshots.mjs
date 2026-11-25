import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

export async function verifyScreenshots(app) {
  const manifest = JSON.parse(await fs.readFile(path.join(app, 'src/content/product-screenshots.json'), 'utf8'));
  if (!manifest.syntheticOnly || !manifest.actualReleaseUi || manifest.schemaVersion !== 2) return false;
  if (manifest.disclosure.en !== 'Actual SHOPYXI application screen. All displayed data is synthetic demonstration data.' ||
    manifest.disclosure.ar !== 'شاشة فعلية من تطبيق SHOPYXI. جميع البيانات المعروضة بيانات تجريبية.') return false;
  for (const product of ['pos', 'storefront', 'affiliate', 'commercehub']) {
    for (const locale of ['en', 'ar']) {
      const screen = manifest.products?.[product]?.[locale];
      if (!screen?.alt || !screen.detailAlt || !screen.desktop || !screen.folio || (['pos', 'commercehub'].includes(product) && !screen.desktopOnly)) return false;
      for (const variant of ['desktop', 'mobile', 'folio']) {
        const image = screen[variant];
        if (!image) continue;
        if (!new RegExp(`^/assets/screen-${product}-${locale}-${variant}-[a-f0-9]{12}\\.webp$`).test(image.publicPath)) return false;
        if (image.viewport.width !== (variant === 'mobile' ? 375 : 1440) || image.viewport.height !== (variant === 'mobile' ? 812 : 900)) return false;
        if (!image.dimensions.width || !image.dimensions.height || !/^[a-f0-9]{64}$/.test(image.originalSha256)) return false;
        if (!image.losslessPixelEquality || !image.sourceReleaseDigest || !image.crop ||
          image.crop.width !== image.dimensions.width || image.crop.height !== image.dimensions.height ||
          image.crop.x < 0 || image.crop.y < 0 || image.crop.x + image.crop.width > image.viewport.width ||
          image.crop.y + image.crop.height > image.viewport.height) return false;
        const bytes = await fs.readFile(path.join(app, 'public', image.publicPath));
        if (createHash('sha256').update(bytes).digest('hex') !== image.publicSha256) return false;
      }
    }
  }
  return true;
}
