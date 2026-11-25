import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const { routes } = await import(pathToFileURL(path.join(app, 'src/routes.mjs')).href);
const { createPreview } = await import(pathToFileURL(path.join(app, 'scripts/preview.mjs')).href);
const widths = (process.argv[2] || '1440,1024,768,375,320').split(',').map(Number);
const pages = (process.argv[3] || 'home,platform,pos,storefront,affiliate,commercehub').split(',');
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
for (const width of widths) {
  await page.setViewportSize({ width, height: 900 });
  for (const locale of ['en', 'ar']) {
    for (const pageKey of pages) {
      const route = routes.find(r => r.locale === locale && r.page === pageKey);
      await page.goto(origin + route.path, { waitUntil: 'networkidle' });
      const data = await page.evaluate(() => {
        const stage = document.querySelector('.hero-stage, .platform-stage, .showcase-stage');
        if (!stage) return null;
        const box = stage.getBoundingClientRect();
        const round = n => Math.round(n);
        const rect = el => { const b = el.getBoundingClientRect(); return { x: round(b.x - box.x), y: round(b.y - box.y), w: round(b.width), h: round(b.height) }; };
        return {
          dir: document.documentElement.dir,
          stage: { w: round(box.width), h: round(box.height) },
          items: [...stage.querySelectorAll('.screenshot-window')].map(el => {
            const image = el.querySelector('.window-frame').getBoundingClientRect();
            const caption = el.querySelector('.window-caption').getBoundingClientRect();
            return { cls: el.className.replace('screenshot-window composition-item ', ''), rect: rect(el),
              image: { x: round(image.x - box.x), y: round(image.y - box.y), w: round(image.width), h: round(image.height) },
              caption: { x: round(caption.x - box.x), y: round(caption.y - box.y), w: round(caption.width), h: round(caption.height) } };
          }),
          pageWidth: document.documentElement.clientWidth,
          scrollWidth: document.documentElement.scrollWidth
        };
      });
      console.log(`${width}px ${locale} ${pageKey} ${JSON.stringify(data)}`);
    }
  }
}
await browser.close();
await new Promise(resolve => server.close(resolve));
