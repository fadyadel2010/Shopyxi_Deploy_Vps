import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { createPreview } from '../scripts/preview.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true });
const results = [];
try {
  for (const locale of ['en','ar']) for (const width of [375,1440]) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, serviceWorkers: 'block' });
    const requests = [];
    await context.route('**/*', async route => {
      const url = route.request().url();
      if (!url.startsWith(origin+'/')) throw Error(`Unexpected external request ${url}`);
      if (url.endsWith('.woff2')) await new Promise(resolve => setTimeout(resolve, 600));
      return route.continue();
    });
    await context.addInitScript(() => {
      window.visualMetrics = { shifts: [], lcp: [] };
      new PerformanceObserver(list => {
        for (const entry of list.getEntries()) if (!entry.hadRecentInput)
          window.visualMetrics.shifts.push({ value: entry.value, sources: entry.sources?.map(s => ({ tag: s.node?.tagName, cls: s.node?.className })) });
      }).observe({ type: 'layout-shift', buffered: true });
      new PerformanceObserver(list => {
        for (const entry of list.getEntries()) window.visualMetrics.lcp.push({ time: entry.startTime, tag: entry.element?.tagName });
      }).observe({ type: 'largest-contentful-paint', buffered: true });
    });
    const page = await context.newPage();
    page.on('response', async response => {
      if (response.request().resourceType() !== 'document')
        requests.push({ url: new URL(response.url()).pathname, type: response.request().resourceType(), bytes: +(response.headers()['content-length'] || 0) });
    });
    await page.goto(origin+`/${locale}/`, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    // Two frames allow the layout-shift observer to flush after the final font paint.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const metrics = await page.evaluate(() => {
      const canvas = document.createElement('canvas'), ctx = canvas.getContext('2d');
      const samples = ['Manage store operations.', 'Build your online presence.', 'Explore more channels.', 'أدر عمليات متجرك.', 'ابنِ حضورك الإلكتروني.', 'واستكشف قنوات إضافية.'];
      return { ...window.visualMetrics, samples: samples.map(text => {
        const widths = {};
        for (const family of ['Cairo','Segoe UI','Arial']) { ctx.font = `750 100px "${family}"`; widths[family] = ctx.measureText(text).width; }
        return { text, widths };
      }) };
    });
    results.push({ locale, width, fontDelayMs: 600, cls: metrics.shifts.reduce((n,s) => n+s.value,0), ...metrics, requests,
      requestedBytes: requests.reduce((n,r) => n+r.bytes,0) });
    await context.close();
  }
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
await fs.mkdir(evidence, { recursive: true });
const pass = results.length === 4 && results.every(result => result.cls <= .1);
await fs.writeFile(path.join(evidence, 'visual-performance.json'), JSON.stringify({ pass, method: 'Cold Chromium contexts; local no-store preview; font requests delayed 600ms; CLS must be <=0.1; synthetic laboratory observations, not field CWV.', results }, null, 2));
console.log(JSON.stringify(results.map(({locale,width,cls,requestedBytes}) => ({locale,width,cls,requestedBytes})),null,2));
if (!pass) process.exitCode = 1;
