// Phase 1B review capture: Product Showcase at five widths in both locales.
// Review build only. No production artifact, deployment or screenshot mutation.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { pagePath } from '../src/routes.mjs';
import { createPreview } from './preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'temp/phase1b-capture'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const widths = [1440, 1024, 768, 375, 320];
const locales = ['en', 'ar'];
const results = { startedUtc: new Date().toISOString(), states: [], errors: [] };
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
  const context = await browser.newContext({ serviceWorkers: 'block', deviceScaleFactor: 1 });
  await context.route('**/*', route => {
    if (route.request().url().startsWith(origin + '/')) return route.continue();
    results.errors.push(`external request: ${route.request().url()}`);
    return route.abort();
  });
  const page = await context.newPage();
  page.on('pageerror', error => results.errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') results.errors.push(message.text()); });
  page.on('response', response => { if (response.status() >= 400) results.errors.push(`${response.status()} ${response.url()}`); });

  for (const locale of locales) {
    for (const width of widths) {
      await page.setViewportSize({ width, height: width < 600 ? 900 : 1000 });
      await page.goto(origin + pagePath(locale, 'home'), { waitUntil: 'networkidle' });
      await page.evaluate(() => document.fonts.ready);
      const response = 200;
      await page.locator('.ps-section').scrollIntoViewIfNeeded();
      await page.evaluate(async () => {
        for (const image of document.querySelectorAll('.ps-section img')) {
          image.scrollIntoView({ block: 'center' });
          await image.decode().catch(() => {});
        }
        document.querySelector('.ps-section').scrollIntoView({ block: 'start' });
      });
      await page.waitForTimeout(250);
      const geometry = await page.evaluate(() => {
        const rect = el => { const b = el.getBoundingClientRect(); return { x: +b.x.toFixed(1), y: +b.y.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
        const section = document.querySelector('.ps-section');
        const cards = [...section.querySelectorAll('.ps-card')];
        return {
          lang: document.documentElement.lang,
          dir: document.documentElement.dir,
          viewport: { w: document.documentElement.clientWidth, h: innerHeight },
          pageScrollWidth: document.documentElement.scrollWidth,
          section: rect(section),
          grid: getComputedStyle(section.querySelector('.ps-grid')).gridTemplateColumns,
          gridHeight: rect(section.querySelector('.ps-grid')).h,
          cards: cards.map(card => {
            const body = card.querySelector('.ps-card-body');
            const img = card.querySelector('img');
            const link = card.querySelector('.ps-card-link');
            const action = card.querySelector('.ps-action');
            return {
              number: card.querySelector('.ps-number')?.textContent,
              title: card.querySelector('.ps-title h3')?.textContent.trim(),
              href: link?.getAttribute('href'),
              description: card.querySelector('.ps-desc')?.textContent,
              card: rect(card),
              bodyColumns: getComputedStyle(body).gridTemplateColumns,
              image: { src: img?.getAttribute('src'), attrW: img?.getAttribute('width'), attrH: img?.getAttribute('height'), naturalW: img?.naturalWidth, naturalH: img?.naturalHeight, complete: img?.complete, display: img ? rect(img) : null },
              link: link ? rect(link) : null,
              action: action ? rect(action) : null,
              window: rect(card.querySelector('.ps-window')),
              overflow: card.scrollWidth > card.clientWidth + 1
            };
          }),
          heading: section.querySelector('h2')?.textContent,
          eyebrow: section.querySelector('.ps-eyebrow')?.textContent,
          sub: section.querySelector('.ps-sub')?.textContent,
          disclosure: section.querySelector('.screen-disclosure')?.textContent
        };
      });
      const base = `${locale}-${width}`;
      await page.locator('.ps-section').screenshot({ path: path.join(evidence, 'screenshots', `${base}-showcase.png`) });
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${base}-viewport.png`) });
      if (width === 1440) await page.screenshot({ path: path.join(evidence, 'screenshots', `${base}-full.png`), fullPage: true });
      results.states.push({ locale, width, http: response, ...geometry });
      console.log(`captured ${base} section=${geometry.section.w}x${geometry.section.h} grid=${geometry.grid}`);
    }
  }
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
await fs.writeFile(path.join(evidence, 'capture.json'), JSON.stringify(results, null, 2) + String.fromCharCode(10));
console.log(JSON.stringify({ states: results.states.length, errors: results.errors }, null, 2));
if (results.errors.length) process.exitCode = 1;
