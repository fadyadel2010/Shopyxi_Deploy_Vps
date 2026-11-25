// Phase 1A evidence capture: viewport + full-page screenshots and geometry for
// the header/hero scope. Serves whichever MARKETING_DIST build is present.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { pagePath } from '../src/routes.mjs';
import { createPreview } from './preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.resolve(process.argv[2] || path.join(app, 'temp', 'phase1a-capture'));
const widths = (process.argv[3] || '1440,1024,768,375,320').split(',').map(Number);
const pages = (process.argv[4] || 'home').split(',');
const locales = (process.argv[5] || 'en,ar').split(',');
const textScale = Number(process.argv[6] || 100);
await fs.mkdir(out, { recursive: true });

const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
const context = await browser.newContext({ serviceWorkers: 'block', deviceScaleFactor: 1 });
const page = await context.newPage();
const metrics = [];
try {
  for (const locale of locales) for (const name of pages) for (const width of widths) {
    const url = origin + pagePath(locale, name);
    await page.setViewportSize({ width, height: width < 600 ? 812 : 900 });
    await page.goto(url, { waitUntil: 'networkidle' });
    if (textScale !== 100) await page.evaluate(v => document.documentElement.style.setProperty('font-size', `${v}%`, 'important'), textScale);
    await page.evaluate(() => document.fonts.ready);
    await page.evaluate(async () => {
      for (const img of document.images) { img.scrollIntoView(); await img.decode().catch(() => {}); }
      scrollTo(0, 0);
    });
    await page.waitForTimeout(120);
    const suffix = textScale === 100 ? '' : `-text${textScale}`;
    const base = `${locale}-${name}-${width}${suffix}`;
    await page.screenshot({ path: path.join(out, `${base}-fold.png`) });
    await page.screenshot({ path: path.join(out, `${base}-full.png`), fullPage: true });
    metrics.push(await page.evaluate(({ base, textScale }) => {
      const r = el => { if (!el) return null; const b = el.getBoundingClientRect(); return { x: +b.x.toFixed(1), y: +b.y.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
      const visible = el => el && el.checkVisibility() && getComputedStyle(el).visibility !== 'hidden';
      const h1 = document.querySelector('main h1');
      const tiles = [...document.querySelectorAll('.product-nav .product-tile')].map(el => ({ ...r(el), name: el.querySelector('.product-tile-name')?.textContent }));
      const windows = [...document.querySelectorAll('.hero-windows .hero-window')].map(el => {
        const frame = el.querySelector('.hero-window-frame');
        return { role: el.dataset.windowRole, ...r(el), frame: r(frame), img: el.querySelector('img')?.getAttribute('src') };
      });
      const targets = [...document.querySelectorAll('header a, header summary, header button, .hero-actions a, .hero-actions button, .product-tile')]
        .filter(visible).map(el => { const b = el.getBoundingClientRect(); return { tag: el.tagName, text: (el.getAttribute('aria-label') || el.textContent).trim().slice(0, 40), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; });
      return {
        state: base, textScale, locale: document.documentElement.lang, dir: document.documentElement.dir,
        viewport: { w: innerWidth, h: innerHeight }, scrollWidth: document.documentElement.scrollWidth,
        documentHeight: document.documentElement.scrollHeight,
        overflowX: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
        header: r(document.querySelector('header.header')),
        hero: r(document.querySelector('.hero')),
        copy: r(document.querySelector('.hero-copy')),
        visual: r(document.querySelector('.hero-visual')),
        h1: h1 ? { ...r(h1), fontSize: getComputedStyle(h1).fontSize, lineHeight: getComputedStyle(h1).lineHeight } : null,
        h1Text: h1?.innerText.replace(/\n/g, ' / '),
        tiles, windows,
        tileRows: [...new Set(tiles.map(t => t.y))].length,
        smallTargets: targets.filter(t => t.w < 44 || t.h < 44),
        targetCount: targets.length,
        h1Count: document.querySelectorAll('h1').length,
        horizontalOverflowNodes: [...document.querySelectorAll('body *')].filter(visible).filter(el => {
          if (el.classList.contains('skip') && el !== document.activeElement) return false;
          const b = el.getBoundingClientRect();
          return b.width > 0 && (b.left < -1 || b.right > document.documentElement.clientWidth + 1);
        }).slice(0, 8).map(el => ({ tag: el.tagName, cls: el.getAttribute('class'), text: el.textContent.trim().slice(0, 50) }))
      };
    }, { base, textScale }));
  }
} finally {
  await context.close();
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
const file = path.join(out, `metrics-text${textScale}.json`);
await fs.writeFile(file, JSON.stringify(metrics, null, 2) + '\n');
console.log(JSON.stringify({ out, states: metrics.length, overflow: metrics.filter(m => m.overflowX).map(m => m.state), file }, null, 2));
