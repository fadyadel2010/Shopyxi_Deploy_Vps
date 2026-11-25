import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { routes } from '../src/routes.mjs';
import { createPreview } from '../scripts/preview.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true });
const receipt = JSON.parse(await fs.readFile(path.join(app, 'dist/build-receipt.json'), 'utf8'));
const results = { browser: browser.version(), buildAssets: receipt.assets, samples: [], errors: [], externalRequests: [] };
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block' });
  await context.route('**/*', route => {
    if (route.request().url().startsWith(origin+'/')) return route.continue();
    results.externalRequests.push(route.request().url()); return route.abort();
  });
  const page = await context.newPage();
  page.on('pageerror', e => results.errors.push(e.message));
  page.on('console', m => { if (m.type()==='error') results.errors.push(m.text()); });
  for (const route of routes) {
    await page.goto(origin+route.path, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    const samples = await page.evaluate(() => [...document.querySelectorAll('h1,h2,h3,main p,.eyebrow,.folio-commercehub .folio-label strong,#locale,.folio-top bdi')]
      .filter(e => e.checkVisibility() && e.textContent.trim()).map((e,i) => {
        e.setAttribute('data-font-inspection', String(i));
        return { index:i, tag:e.tagName, text:e.textContent, family:getComputedStyle(e).fontFamily,
          weight:getComputedStyle(e).fontWeight, lineHeight:getComputedStyle(e).lineHeight,
          letterSpacing:getComputedStyle(e).letterSpacing, mixedScript:/[\u0600-\u06ff]/.test(e.textContent) && /[A-Za-z]/.test(e.textContent) };
      }));
    const session = await context.newCDPSession(page);
    await session.send('DOM.enable'); await session.send('CSS.enable');
    const doc = await session.send('DOM.getDocument');
    for (const sample of samples) {
      // CDP reports fonts for a node's own text, not necessarily nested links/bdi.
      // Inspect the complete rendered subtree rather than treating an empty parent
      // glyph list as a font failure.
      const selector = `[data-font-inspection="${sample.index}"]`;
      const { nodeIds } = await session.send('DOM.querySelectorAll', {
        nodeId: doc.root.nodeId, selector: `${selector}, ${selector} *`
      });
      const fonts = [];
      for (const nodeId of nodeIds) {
        fonts.push(...(await session.send('CSS.getPlatformFontsForNode', { nodeId })).fonts);
      }
      results.samples.push({ route:route.path, ...sample, fonts,
        pass:fonts.length>0 && fonts.every(f => f.isCustomFont && f.familyName==='Cairo') });
    }
    await session.detach();
  }
  await context.close();
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
results.pass = !results.errors.length && !results.externalRequests.length && results.samples.length>100 && results.samples.every(s=>s.pass);
await fs.mkdir(evidence,{recursive:true});
await fs.writeFile(path.join(evidence,'rendered-fonts.json'),JSON.stringify(results,null,2));
console.log(JSON.stringify({pass:results.pass,samples:results.samples.length,mixedScriptSamples:results.samples.filter(s=>s.mixedScript).length,failures:results.samples.filter(s=>!s.pass),errors:results.errors},null,2));
if (!results.pass) process.exitCode=1;
