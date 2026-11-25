import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { routes } from '../src/routes.mjs';
import { createPreview } from './preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.resolve(process.argv[2] || path.join(app, 'temp', 'refine03'));
const widths = (process.argv[3] || '1440,1024,768,375,320').split(',').map(Number);
await fs.mkdir(out, { recursive: true });
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
const context = await browser.newContext({ serviceWorkers: 'block', deviceScaleFactor: 1 });
const page = await context.newPage();
try {
  for (const route of routes) {
    for (const width of widths) {
      await page.setViewportSize({ width, height: width < 600 ? 812 : 900 });
      await page.goto(origin + route.path, { waitUntil: 'networkidle' });
      await page.evaluate(() => document.fonts.ready);
      await page.evaluate(async () => {
        for (const img of document.images) {
          if (!img.checkVisibility()) continue;
          img.scrollIntoView();
          await img.decode().catch(() => {});
        }
        scrollTo(0, 0);
      });
      const base = `${route.locale}-${route.page}-${width}`;
      await page.screenshot({ path: path.join(out, `${base}-full.png`), fullPage: true });
      await page.screenshot({ path: path.join(out, `${base}-viewport.png`), fullPage: false });
    }
  }
} finally {
  await context.close();
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
console.log('captured', out);
