// Owner-approved lean rendering qualification. Candidate HTML is served from memory,
// never exported as a public build before the actual release guard is satisfied.
import fs from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { chromium, firefox, webkit } from 'playwright';
import { routes, csp, siteOrigin, rootRedirect } from '../src/routes.mjs';
import { publicClaim } from '../src/publication.mjs';
import { releaseInputs } from '../scripts/release-inputs.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output/public'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const require = createRequire(import.meta.url);
const { render, renderRoot } = require(path.join(app, '.build/render.cjs'));
const receipt = JSON.parse(await fs.readFile(path.join(app, 'dist/build-receipt.json'), 'utf8'));
const assets = receipt.assets;
const claims = JSON.parse(await fs.readFile(path.join(app, 'src/content/claims.json'), 'utf8'));
const publicClaims = Object.fromEntries(Object.entries(claims).map(([id, claim]) => {
  try { return [id, { en: publicClaim(claims, id, 'en'), ar: publicClaim(claims, id, 'ar') }]; }
  catch { return [id, {}]; }
}));
const screenshotManifest = JSON.parse(await fs.readFile(path.join(app, 'src/content/product-screenshots.json'), 'utf8'));
const screenshotIndex = Object.fromEntries(Object.values(screenshotManifest.products).flatMap(product =>
  Object.values(product).flatMap(screen => ['desktop', 'mobile', 'folio'].map(key => screen[key]).filter(Boolean)
    .map(image => [image.publicPath, image]))));
const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
const result = { startedUtc: new Date().toISOString(), states: [], axe: [], errors: [], consoleErrors: [],
  externalRequests: [], brokenResponses: [], assertions: [], fonts: [], screenshots: [], browserVersions: {} };
const qualificationInputs = await releaseInputs(app);
result.sourceSha256 = qualificationInputs.sourceSha256;
result.inputsSha256 = qualificationInputs.inputsSha256;
const check = (pass, label, detail) => result.assertions.push({ pass: !!pass, label, ...(detail === undefined ? {} : { detail }) });
let server;
let origin = process.env.MARKETING_ORIGIN;
if (!origin) {
  const html = new Map(routes.map(r => [r.path, render(r, assets, true)]));
  html.set('/', renderRoot(assets, true));
  server = http.createServer(async (req, res) => {
    res.setHeader('Content-Security-Policy', csp + "; frame-ancestors 'none'");
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Referrer-Policy', 'no-referrer');
    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (pathname === '/') {
      res.writeHead(rootRedirect.status, { Location: rootRedirect.location,
        'Cache-Control': rootRedirect.cacheControl, 'X-Robots-Tag': rootRedirect.robots });
      res.end(); return;
    }
    if (html.has(pathname)) { res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(html.get(pathname)); return; }
    if (/^\/assets\/[a-zA-Z0-9._-]+$/.test(pathname)) {
      try {
        const type = { '.css': 'text/css', '.js': 'application/javascript', '.woff2': 'font/woff2', '.png': 'image/png', '.webp': 'image/webp', '.txt': 'text/plain' }[path.extname(pathname)];
        res.setHeader('Content-Type', type || 'application/octet-stream');
        res.end(await fs.readFile(path.join(app, 'dist', pathname)));
        return;
      } catch {}
    }
    res.statusCode = 404; res.end('Not found');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
}
result.origin = origin.startsWith('http://127.0.0.1:') ? 'private in-memory public-render candidate' : origin;
try {
  for (const [name, engine] of Object.entries({ chromium, firefox, webkit })) {
    const browser = await engine.launch({ headless: true });
    result.browserVersions[name] = browser.version();
    try {
      const context = await browser.newContext({ serviceWorkers: 'block' });
      await context.route('**/*', route => {
        if (route.request().url().startsWith(origin + '/') && ['GET', 'HEAD'].includes(route.request().method())) return route.continue();
        result.externalRequests.push({ engine: name, url: route.request().url() }); return route.abort();
      });
      const page = await context.newPage();
      page.on('pageerror', e => result.errors.push({ engine: name, message: e.message }));
      page.on('console', m => { if (m.type() === 'error') result.consoleErrors.push({ engine: name, text: m.text() }); });
      page.on('response', r => { if (r.status() >= 400) result.brokenResponses.push({ engine: name, url: r.url(), status: r.status() }); });
      for (const route of routes) {
        for (const width of [320, 375, 768, 1024, 1440]) {
          await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
          const response = await page.goto(origin + route.path, { waitUntil: 'networkidle' });
          await page.evaluate(() => document.fonts.ready);
           await page.evaluate(async () => {
             for (const image of document.querySelectorAll('img[src^="/assets/screen-"]')) {
               image.scrollIntoView();
               await new Promise(resolve => requestAnimationFrame(resolve));
               // Firefox can cancel decode while a responsive image request
               // changes. The load/natural-size assertions below remain mandatory.
               await image.decode().catch(() => {});
             }
           });
           await page.waitForFunction(() => [...document.querySelectorAll('img[src^="/assets/screen-"]')].every(i => i.complete && i.naturalWidth > 0));
          await page.evaluate(() => window.scrollTo(0, 0));
          check(response.status() === 200, `${name} ${route.path} ${width} status`);
          const state = await page.evaluate(({ locale, dir, claims }) => {
            const norm = s => s.replace(/\s+/g, ' ').trim();
            const ids = [...document.querySelectorAll('[id]')].map(e => e.id);
            const links = [...document.querySelectorAll('a[href]')].map(e => e.getAttribute('href'));
            const altered = [...document.querySelectorAll('[data-claim]')].filter(e =>
              e.closest('body') && norm(e.textContent) !== norm(claims[e.dataset.claim]?.[locale] || '')).map(e => e.dataset.claim);
            return {
              lang: document.documentElement.lang, dir: document.documentElement.dir,
              viewport: document.documentElement.clientWidth, scrollWidth: document.documentElement.scrollWidth,
              h1s: document.querySelectorAll('h1').length,
              forbidden: document.querySelectorAll('.review,.placeholder,form,input,textarea,select,button,[data-review-only],[data-approval="PENDING"],a[href="#demo"]').length,
              brokenAnchors: links.filter(h => h.startsWith('#') && !ids.includes(h.slice(1))),
              altered, cookies: document.cookie, localStorage: localStorage.length, sessionStorage: sessionStorage.length,
              canonical: document.querySelector('link[rel=canonical]')?.href,
              isPublic: document.body.dataset.publicRelease === 'lean',
               cairoLoaded: document.fonts.check('400 16px Cairo'),
               screenshotDisclosures: [...document.querySelectorAll('.screen-disclosure')].map(e => e.textContent),
                 productScreens: [...document.querySelectorAll('img[src^="/assets/screen-"]')].map(image => {
                   return { alt: image.alt, loaded: image.complete && image.naturalWidth > 0,
                     currentSrc: image.currentSrc, src: image.getAttribute('src'),
                     naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight,
                     width: image.width, height: image.height,
                     linked: image.closest('a')?.getAttribute('href') };
               }),
               expected: { locale, dir }
            };
          }, { locale: route.locale, dir: route.dir, claims: publicClaims });
          state.engine = name; state.route = route.path; state.width = width;
            // Current composition: hero/showcase/CTA plus product-page related captures.
            const expectedScreens = route.page === 'home' ? 15 : route.page === 'platform' ? 9 : route.page === 'commercehub' ? 5 : 6;
          const screenDisclosure = route.locale === 'en' ? 'Actual SHOPYXI application screen. All displayed data is synthetic demonstration data.' :
            'شاشة فعلية من تطبيق SHOPYXI. جميع البيانات المعروضة بيانات تجريبية.';
          check(state.productScreens.length === expectedScreens && state.productScreens.every(image =>
             image.loaded && image.alt &&
             image.naturalWidth === screenshotIndex[new URL(image.currentSrc).pathname]?.dimensions.width &&
             image.naturalHeight === screenshotIndex[new URL(image.currentSrc).pathname]?.dimensions.height &&
             (!image.linked || image.linked.startsWith('/assets/screen-')) && image.currentSrc.includes(`-${route.locale}-`)) &&
             state.screenshotDisclosures.length > 0 && state.screenshotDisclosures.every(text => text === screenDisclosure),
           `${name} ${route.path} ${width} genuine crop load/disclosure/locale/link`);
          state.pass = state.lang === route.locale && state.dir === route.dir && state.scrollWidth <= state.viewport + 1 &&
            state.h1s === 1 && !state.forbidden && !state.brokenAnchors.length && !state.altered.length &&
            !state.cookies && !state.localStorage && !state.sessionStorage && state.canonical === siteOrigin + route.path && state.isPublic && state.cairoLoaded;
          result.states.push(state);
          if ([375, 1440].includes(width)) {
            await page.evaluate(axe);
            const audit = await page.evaluate(async () => {
              const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
              return { violations: a.violations, incomplete: a.incomplete };
            });
            result.axe.push({ engine: name, route: route.path, width, ...audit });
            // WebKit's screenshot machinery itself injects CSP-blocked styles,
            // even with caret='initial' (retained diagnostic). Capture Chromium;
            // still require clean real-document/axe/interaction checks in all engines.
            if (name === 'chromium') {
               const file = `screenshots/${name}-${route.locale}-${route.page}-${width}.png`;
               await page.screenshot({ path: path.join(evidence, file), fullPage: false });
               result.screenshots.push(file);
               const fullFile = file.replace('.png', '-full.png');
               await page.screenshot({ path: path.join(evidence, fullFile), fullPage: true });
               result.screenshots.push(fullFile);
             }
          }
        }
        await page.setViewportSize({ width: 375, height: 812 });
        await page.goto(origin + route.path, { waitUntil: 'networkidle' });
        await page.locator('#locale').focus(); await page.keyboard.press('Enter');
        await page.waitForURL(origin + route.equivalent + '#locale');
        check(await page.locator('html').getAttribute('lang') !== route.locale, `${name} equivalent language ${route.path}`);
        const mobile = page.locator('.mobile-nav > summary');
        await mobile.focus(); await page.keyboard.press('Enter');
        check(await page.locator('.mobile-nav').getAttribute('open') !== null, `${name} keyboard mobile open ${route.path}`);
        await page.keyboard.press('Escape');
        check(await page.locator('.mobile-nav').getAttribute('open') === null &&
          await mobile.evaluate(e => e === document.activeElement), `${name} keyboard mobile close/focus ${route.path}`);
        await page.emulateMedia({ reducedMotion: 'reduce' });
        await page.evaluate(() => document.documentElement.style.fontSize = '200%');
           check(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1), `${name} 200% mobile ${route.path}`);
        await page.emulateMedia({ reducedMotion: 'no-preference' });
      }
       const rootResponse = await page.goto(origin + '/', { waitUntil: 'networkidle' });
       check(page.url() === origin + '/ar/' && rootResponse.status() === 200, `${name} root redirects to Arabic`);
       if (name === 'chromium') {
        for (const locale of ['en', 'ar']) {
          await page.goto(origin + `/${locale}/`, { waitUntil: 'networkidle' });
          await page.evaluate(() => document.fonts.ready);
          const session = await context.newCDPSession(page);
          await session.send('DOM.enable'); await session.send('CSS.enable');
          const doc = await session.send('DOM.getDocument');
          for (const selector of ['h1', '.hero-copy > p', '.window-name']) {
            const { nodeId } = await session.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector });
            const fonts = await session.send('CSS.getPlatformFontsForNode', { nodeId });
            result.fonts.push({ locale, selector, fonts: fonts.fonts });
            check(fonts.fonts.length && fonts.fonts.every(f => f.familyName === 'Cairo' && f.isCustomFont), `Actual Cairo ${locale} ${selector}`);
          }
          await session.detach();
        }
      }
      await context.close();
    } finally { await browser.close(); }
  }
} catch (error) {
  result.errors.push({ message: error.stack });
} finally {
  if (server) await new Promise(resolve => server.close(resolve));
  result.completedUtc = new Date().toISOString();
  result.inputsUnchanged = (await releaseInputs(app)).inputsSha256 === result.inputsSha256;
  result.summary = { states: result.states.length, failedStates: result.states.filter(s => !s.pass).length,
    assertions: result.assertions.length, failedAssertions: result.assertions.filter(a => !a.pass).length,
    axeAudits: result.axe.length, axeViolations: result.axe.reduce((n, a) => n + a.violations.length, 0),
    axeIncompleteAudits: result.axe.filter(a => a.incomplete.length).length,
    consoleErrors: result.consoleErrors.length, errors: result.errors.length, externalRequests: result.externalRequests.length,
    brokenResponses: result.brokenResponses.length, screenshots: result.screenshots.length };
  result.pass = result.inputsUnchanged && result.states.length === 180 && result.axe.length === 72 && !Object.entries(result.summary).some(([k, n]) =>
    /failed|Violations|Incomplete|consoleErrors|^errors$|externalRequests|brokenResponses/.test(k) && n);
  await fs.writeFile(path.join(evidence, 'public-browser-results.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify({ pass: result.pass, summary: result.summary, failures: result.states.filter(s => !s.pass), errors: result.errors }, null, 2));
  if (!result.pass) process.exitCode = 1;
}
