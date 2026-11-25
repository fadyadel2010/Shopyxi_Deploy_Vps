import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const { routes } = await import(pathToFileURL(path.join(app, 'src/routes.mjs')).href);
const { createPreview } = await import(pathToFileURL(path.join(app, 'scripts/preview.mjs')).href);
const out = path.resolve(process.argv[2] || path.join(app, 'temp', 'closeups'));
const widths = (process.argv[3] || '1440,1024,768,375').split(',').map(Number);
const locales = (process.argv[4] || 'en,ar').split(',');
const pages = (process.argv[5] || 'home,platform,pos,storefront,affiliate,commercehub').split(',');
const fs = await import('node:fs/promises');
await fs.mkdir(out, { recursive: true });
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
try {
  for (const route of routes.filter(r => locales.includes(r.locale) && pages.includes(r.page))) {
    for (const width of widths) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(origin + route.path, { waitUntil: 'networkidle' });
      await page.evaluate(() => document.fonts.ready);
      await page.evaluate(async () => { for (const img of document.images) { img.scrollIntoView(); await img.decode().catch(() => {}); } scrollTo(0, 0); });
      const section = route.page === 'home' ? 'section.hero' : route.page === 'platform' ? 'section.platform-hero' : 'section.product-hero';
      await page.locator(section).screenshot({ path: path.join(out, `${route.locale}-${route.page}-${width}-section.png`) });
      const comp = route.page === 'home' ? '.hero-composition' : route.page === 'platform' ? '.platform-composition' : '.product-showcase';
      await page.locator(comp).screenshot({ path: path.join(out, `${route.locale}-${route.page}-${width}-composition.png`) });
    }
  }
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
console.log('closeups', out);
