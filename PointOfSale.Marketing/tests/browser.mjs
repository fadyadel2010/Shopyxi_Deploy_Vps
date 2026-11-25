import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { routes, accessDestinations, pagePath, siteOrigin, registrationDestination } from '../src/routes.mjs';
import { createPreview } from '../scripts/preview.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const claims = JSON.parse(await fs.readFile(path.join(app, 'src/content/claims.json'), 'utf8'));
const labels = JSON.parse(await fs.readFile(path.join(app, 'src/content/labels.json'), 'utf8'));
const require = createRequire(import.meta.url);
const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const results = { browser: '', origin: 'marketing-only loopback (ephemeral port)', states: [], assertions: [], axe: [], screenshots: [], errors: [], consoleErrors: [], externalRequests: [], brokenResponses: [], fonts: [] };
const smoke = process.argv.includes('--smoke');
const check = (condition, label, detail) => results.assertions.push({ label, pass: Boolean(condition), ...(detail === undefined ? {} : { detail }) });
let browser;
let page;
try {
  browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
  results.browser = browser.version();
  const context = await browser.newContext({ serviceWorkers: 'block' });
  await context.route('**/*', route => {
    if (route.request().url().startsWith(origin + '/')) return route.continue();
    results.externalRequests.push(route.request().url()); return route.abort();
  });
  page = await context.newPage();
  page.on('pageerror', e => results.errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') results.consoleErrors.push(m.text()); });
  page.on('response', r => { if (r.status() >= 400) results.brokenResponses.push({ url: r.url(), status: r.status() }); });

  async function load(route, width, height = 900, text = 100) {
    await page.setViewportSize({ width, height });
    const response = await page.goto(origin + route.path, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    check(response.status() === 200, `Direct load ${route.path} ${width}/${text}`);
    if (text !== 100) await page.evaluate(value => document.documentElement.style.setProperty('font-size', `${value}%`, 'important'), text);
  }
  async function inspect(route, mode) {
    const result = await page.evaluate(({ claims, expected, boundaries }) => {
      const lang = document.documentElement.lang;
      const norm = s => s.replace(/\s+/g, ' ').trim();
      const visible = e => e.checkVisibility() && getComputedStyle(e).visibility !== 'hidden';
      const width = document.documentElement.clientWidth;
      const outside = [...document.querySelectorAll('body *')].filter(visible).filter(e => {
        if (e.classList.contains('skip') && e !== document.activeElement) return false;
        // Decorative aria-hidden glows may paint a zero-opacity box beyond their
        // clipping container; if an ancestor clips them they cannot cause visible
        // horizontal overflow, so they must not fail this layout check.
        if (e.getAttribute('aria-hidden') === 'true') {
          for (let a = e.parentElement; a; a = a.parentElement) {
            const cs = getComputedStyle(a);
            if (cs.overflowX !== 'visible' || cs.overflowY !== 'visible') return false;
          }
        }
        const b = e.getBoundingClientRect(); return b.width > 0 && (b.left < -1 || b.right > width + 1);
      }).map(e => ({ tag: e.tagName, cls: e.getAttribute('class'), text: e.textContent.slice(0, 90) }));
      const altered = [...document.querySelectorAll('body [data-claim]')].filter(e => norm(e.textContent) !== norm(claims[e.dataset.claim]?.[lang] || '')).map(e => e.dataset.claim);
      const badDisclosure = [...document.querySelectorAll('.placeholder strong')].filter(e => e.textContent !== expected).length;
      const imgs = [...document.images].filter(i => i.checkVisibility()).map(i => {
        const logo = i.getAttribute('src') === '/assets/shopyxi-logo.png';
        const b = i.getBoundingClientRect();
        const inView = b.top < innerHeight && b.bottom > 0;
        return { src: i.getAttribute('src'), ok: logo ? i.complete && i.naturalWidth === 2400 && i.naturalHeight === 747
          : i.hasAttribute('width') && i.hasAttribute('height') && i.hasAttribute('alt') && (!inView || i.complete && i.naturalWidth > 0) };
      });
      const headingLevels = [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(e => +e.tagName[1]);
      const headingSkips = headingLevels.filter((n, i) => i && n > headingLevels[i - 1] + 1);
      const hero = document.querySelector('.hero-grid');
      const heroChildren = hero ? [...hero.children].map(e => e.getBoundingClientRect()) : [];
      const mobileStageStacked = !hero || width > 896 || heroChildren[1].top >= heroChildren[0].bottom - 1;
      return { lang, dir: document.documentElement.dir, width, height: innerHeight, rootFontSize: getComputedStyle(document.documentElement).fontSize,
        scrollWidth: document.documentElement.scrollWidth, outside, altered, badDisclosure, imgs, headingSkips, mobileStageStacked,
        h1s: document.querySelectorAll('h1').length, fields: document.querySelectorAll('form,input,textarea,select').length,
        missingBoundaries: boundaries.filter(id => !document.querySelector(`body [data-claim="${id}"]`)),
        localStorage: localStorage.length, sessionStorage: sessionStorage.length, cookies: document.cookie,
        reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
        reviewVisible: document.querySelector('.review').checkVisibility(),
        sources: [...document.querySelectorAll('[src]')].map(e => e.getAttribute('src')) };
    }, {
      claims, expected: labels[route.locale].placeholder,
      boundaries: ['home', 'platform'].includes(route.page) ? ['C06', 'C31', 'C35'] : route.page === 'commercehub' ? ['C31', 'C35'] : route.page === 'affiliate' ? ['C23', 'C24', 'C29', 'C38'] : route.page === 'storefront' ? ['C19', 'C27', 'C30'] : ['C09', 'C14', 'C15']
    });
    result.route = route.path; result.mode = mode;
    result.pass = result.lang === route.locale && result.dir === route.dir && result.scrollWidth <= result.width + 1 && !result.outside.length && !result.altered.length && !result.badDisclosure && result.h1s === 1 && result.fields === 0 && !result.missingBoundaries.length && !result.headingSkips.length && result.imgs.every(i => i.ok) && !result.localStorage && !result.sessionStorage && !result.cookies && result.reviewVisible && result.mobileStageStacked;
    results.states.push(result);
  }
  // Request-level route/refresh behavior and standards metadata without JavaScript.
  const noJS = await browser.newContext({ javaScriptEnabled: false, serviceWorkers: 'block' });
  await noJS.route('**/*', route => route.request().url().startsWith(origin + '/') ? route.continue() : route.abort());
  const crawler = await noJS.newPage();
  const titles = [], descriptions = [];
  for (const route of routes) {
    const response = await crawler.goto(origin + route.path);
    check(response.status() === 200 && response.headers()['x-robots-tag'].includes('noindex'), `Crawler HTML ${route.path}`);
    check(await crawler.locator('h1').count() === 1 && (await crawler.locator('main').innerText()).length > 1000, `Static substantive content ${route.path}`);
    check((await crawler.reload()).status() === 200, `Direct refresh ${route.path}`);
    check(await crawler.locator('html').getAttribute('lang') === route.locale && await crawler.locator('html').getAttribute('dir') === route.dir, `Crawler lang/dir ${route.path}`);
    check(await crawler.locator('link[rel=canonical]').getAttribute('href') === siteOrigin + route.path, `Canonical ${route.path}`);
    for (const locale of ['en', 'ar']) check(await crawler.locator(`link[hreflang="${locale}"]`).getAttribute('href') === siteOrigin + pagePath(locale, route.page), `Hreflang ${route.path} → ${locale}`);
    titles.push(await crawler.title()); descriptions.push(await crawler.locator('meta[name=description]').getAttribute('content'));
    check(await crawler.locator('meta[property="og:title"]').getAttribute('content') === await crawler.title(), `OG title ${route.path}`);
    const links = await crawler.locator('body a[href]').evaluateAll(links => links.map(a => a.getAttribute('href')));
    const ids = await crawler.locator('[id]').evaluateAll(nodes => nodes.map(e => e.id));
    for (const href of new Set(links)) {
      if (href.startsWith('https:')) { check([...accessDestinations, registrationDestination].includes(href), `Exact ordinary access href ${route.path}`); continue; }
      const u = new URL(href, origin + route.path);
      check(u.origin === origin, `Internal link origin ${href}`);
      if (u.pathname === route.path && u.hash) check(ids.includes(u.hash.slice(1)), `Anchor resolves ${route.path}${u.hash}`);
      else {
        const res = await fetch(origin + u.pathname); check(res.status === 200, `Internal path resolves ${u.pathname}`);
      }
    }
    check((await fetch(origin + route.path.replace(/\/$/, '') + '/')).status === 200, `Directory alias ${route.path}`);
  }
  check(new Set(titles).size === 12 && new Set(descriptions).size === 12, 'Twelve unique localized titles/descriptions');
  await crawler.goto(origin + '/');
  check(new URL(crawler.url()).pathname === '/ar/' && await crawler.locator('html').getAttribute('lang') === 'ar', 'Root immediately selects Arabic without a chooser');
  await noJS.close();

  const testedRoutes = smoke ? routes.filter(r => r.page === 'home') : routes;
  const widths = smoke ? [375, 1440] : [320, 375, 768, 1024, 1440];
  for (const route of testedRoutes) {
    for (const width of widths) {
      await load(route, width, width === 375 ? 812 : 900);
      await inspect(route, 'default');
      if ([375, 1440].includes(width)) {
        const base = `${route.locale}-${route.page}-${width}`;
        // Genuine full-page capture must load lazy images before capture, without changing production loading policy.
        await page.evaluate(async () => {
          for (const img of document.images) {
            if (!img.checkVisibility()) continue;
            img.scrollIntoView();
            await img.decode().catch(() => {});
          }
          scrollTo(0, 0);
        });
        for (const full of [false, true]) {
          const filename = `screenshots/${base}-${full ? 'full' : 'viewport'}.png`;
          await page.screenshot({ path: path.join(evidence, filename), fullPage: full });
          results.screenshots.push({ route: route.path, width, file: filename, fullPage: full });
        }
        // DevTools evaluation of local test tooling; do not weaken the website CSP.
        await page.evaluate(axeSource);
        const audit = await page.evaluate(async () => {
          const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice'] } });
          return { violations: a.violations, incomplete: a.incomplete, passedRules: a.passes.map(x => x.id) };
        });
        results.axe.push({ route: route.path, width, ...audit });
      }
      if (!smoke) {
        await load(route, width, 900, 200);
        await inspect(route, '200-percent-root-text');
        if (route.page === 'home' && [375, 1440].includes(width)) {
          const filename = `screenshots/${route.locale}-home-${width}-text-200-full.png`;
          await page.screenshot({ path: path.join(evidence, filename), fullPage: true });
          results.screenshots.push({ route: route.path, width, file: filename, fullPage: true, textSizing: 200 });
        }
      }
    }
    if (!smoke) {
      await load(route, 812, 375); await inspect(route, 'landscape');
      if (route.page === 'home') {
        const filename = `screenshots/${route.locale}-home-812-landscape.png`;
        await page.screenshot({ path: path.join(evidence, filename), fullPage: false });
        results.screenshots.push({ route: route.path, width: 812, file: filename, fullPage: false });
      }
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await load(route, 375, 812); await inspect(route, 'reduced-motion');
      check(await page.locator('.actions a').first().evaluate(e => getComputedStyle(e).transitionDuration.split(',').every(x => parseFloat(x) === 0)), `Reduced motion ${route.path}`);
      await page.emulateMedia({ reducedMotion: 'no-preference' });
    }
    for (const width of [375, 1440]) {
      await load(route, width);
      // Keyboard, not synthetic click, opens and dismisses both disclosure types.
      const access = page.locator('#customer-access > summary');
      await access.focus(); await page.keyboard.press('Enter');
      check(await page.locator('#customer-access').getAttribute('open') !== null, `Access Enter ${route.path} ${width}`);
      check(await page.locator('#customer-access .access-panel a').evaluateAll(nodes => nodes.every(e => {
        const r = e.getBoundingClientRect(); return r.width >= 44 && r.height >= 44;
      })), `Access link target sizes ${route.path} ${width}`);
      await page.keyboard.press('Tab');
      check(await page.evaluate(() => document.activeElement.getAttribute('href')) === accessDestinations[0], `Access tab order ${route.path} ${width}`);
      await page.keyboard.press('Escape');
      check(await page.locator('#customer-access').getAttribute('open') === null && await access.evaluate(e => e === document.activeElement), `Access Escape/focus ${route.path} ${width}`);
      await page.keyboard.press('Space');
      check(await page.locator('#customer-access').getAttribute('open') !== null, `Access Space ${route.path} ${width}`);
      await page.locator('#locale').focus();
      check(await page.locator('#customer-access').getAttribute('open') === null, `Focus-out closes access ${route.path} ${width}`);
      if (width === 375) {
        const mobile = page.locator('.mobile-nav > summary');
        await mobile.focus(); await page.keyboard.press('Space');
        check(await page.locator('.mobile-nav').getAttribute('open') !== null, `Mobile Space ${route.path}`);
        await inspect(route, 'mobile-menu-open');
        await page.keyboard.press('Escape');
        check(await mobile.evaluate(e => e === document.activeElement) && await page.locator('.mobile-nav').getAttribute('open') === null, `Mobile Escape/focus ${route.path}`);
        await page.keyboard.press('Enter'); await page.mouse.click(8, 780);
        check(await page.locator('.mobile-nav').getAttribute('open') === null, `Pointer-out closes menu ${route.path}`);
      }
      if (route.page === 'home') {
        const cta = page.locator('.fx-cta');
        check(await cta.getAttribute('href') === registrationDestination, `Free experience destination ${route.path} ${width}`);
        check(await page.locator('#demo, a[href="#demo"]').count() === 0, `No Demo placeholder on the homepage ${route.path} ${width}`);
        check(await cta.evaluate(e => e.getBoundingClientRect().height >= 44), `Free experience target size ${route.path} ${width}`);
      } else {
        await page.locator('a[href="#demo"]').first().focus(); await page.keyboard.press('Enter');
        await page.waitForURL(origin + route.path + '#demo');
        check(await page.evaluate(() => document.activeElement.id) === 'demo', `Demo anchor focus ${route.path} ${width}`);
        check(await page.locator('#demo button').isDisabled() && await page.locator('form,input,textarea').count() === 0, `Non-submitting demo ${route.path} ${width}`);
      }
      await page.locator('#locale').focus(); await page.keyboard.press('Enter');
      await page.waitForURL(origin + route.equivalent + '#locale');
      check(await page.locator('html').getAttribute('lang') !== route.locale && await page.evaluate(() => document.activeElement.id) === 'locale', `Equivalent locale/focus ${route.path} ${width}`);
      // Page navigation uses native links, main fragment and normal history.
      const target = route.page === 'platform' ? 'home' : 'platform';
      await page.locator(`footer a[href="${pagePath(route.locale === 'en' ? 'ar' : 'en', target)}#main"]`).first().focus();
      await page.keyboard.press('Enter');
      await page.waitForURL(origin + pagePath(route.locale === 'en' ? 'ar' : 'en', target) + '#main');
      check(await page.evaluate(() => document.activeElement.id) === 'main', `Page navigation main focus ${route.path} ${width}`);
      await load(route, width);
      // Actual tab traversal; visible focus/control geometry for every reached item.
      await page.locator('.skip').focus();
      let sweep = [];
      for (let i = 0; i < 80; i++) {
        const active = await page.evaluate(() => {
          const e = document.activeElement;
          if (e === document.body) return null;
          const b = e.getBoundingClientRect(), s = getComputedStyle(e);
          return { tag: e.tagName, text: e.textContent.slice(0, 60), width: b.width, height: b.height, outline: s.outlineStyle, outlineWidth: s.outlineWidth };
        });
        if (!active) break;
        sweep.push(active);
        await page.keyboard.press('Tab');
      }
      check(sweep.length >= 15 && sweep.every(x => x.outline !== 'none' && parseFloat(x.outlineWidth) >= 2), `Visible tab focus sweep ${route.path} ${width}`, sweep);
      check(sweep.every(x => x.width >= 43.9 && x.height >= 43.9), `44px keyboard targets ${route.path} ${width}`, sweep.filter(x => x.width < 43.9 || x.height < 43.9));
      check(await page.locator('.actions .button').first().evaluate(e => e.getBoundingClientRect().height >= 48), `48px primary action ${route.path} ${width}`);
      if (width === 375 && !smoke) {
        await load(route, 375, 812, 200);
        const mobile = page.locator('.mobile-nav > summary');
        await mobile.focus(); await page.keyboard.press('Enter'); await inspect(route, '200-percent-mobile-menu-open');
        check(await page.locator('.mobile-nav').getAttribute('open') !== null, `200% mobile keyboard open ${route.path}`);
        await page.keyboard.press('Escape');
        check(await mobile.evaluate(e => e === document.activeElement), `200% mobile Escape restores focus ${route.path}`);
        await access.focus(); await page.keyboard.press('Enter'); await inspect(route, '200-percent-access-menu-open');
        check(await page.locator('#customer-access').getAttribute('open') !== null, `200% access keyboard open ${route.path}`);
        if (route.page === 'home') {
          const filename = `screenshots/${route.locale}-home-375-text-200-access.png`;
          await page.screenshot({ path: path.join(evidence, filename), fullPage: false });
          results.screenshots.push({ route: route.path, width: 375, file: filename, fullPage: false, textSizing: 200 });
        }
        await page.keyboard.press('Escape');
        check(await access.evaluate(e => e === document.activeElement), `200% access Escape restores focus ${route.path}`);
      }
    }
  }
  for (const locale of ['en', 'ar']) {
    await page.goto(origin + `/${locale}/`);
    const session = await context.newCDPSession(page);
    await session.send('DOM.enable'); await session.send('CSS.enable');
    const doc = await session.send('DOM.getDocument');
    await page.evaluate(() => document.fonts.ready);
    for (const selector of ['h1', '.hero-copy > p', '.window-name', '.window-index', '.hero-copy .eyebrow bdi']) {
      const { nodeId } = await session.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector });
      const fonts = await session.send('CSS.getPlatformFontsForNode', { nodeId });
      results.fonts.push({ locale, selector, fonts: fonts.fonts });
      check(fonts.fonts.length > 0 && fonts.fonts.every(f => f.familyName === 'Cairo' && f.isCustomFont), `Actual rendered Cairo ${locale} ${selector}`, fonts.fonts);
    }
    await session.detach();
  }
  // Unknown routes and blocked methods are tested outside browser error accounting.
  check((await fetch(origin + '/en/not-a-page')).status === 404, 'Unknown route is real 404, no SPA fallback');
  check((await fetch(origin + '/en/', { method: 'POST' })).status === 405, 'Preview rejects POST');
  check((await fetch(origin + '/assets/not-an-asset.png')).status === 404, 'Missing assets are real 404');
  await context.close();
} catch (error) {
  results.errors.push(error.stack);
  if (page && !page.isClosed()) {
    results.failureContext = await page.evaluate(() => ({ url: location.href, focus: document.activeElement?.outerHTML })).catch(() => null);
    await page.screenshot({ path: path.join(evidence, 'failure.png'), fullPage: false }).catch(() => {});
  }
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
  results.summary = {
    states: results.states.length, failedStates: results.states.filter(x => !x.pass).length,
    assertions: results.assertions.length, failedAssertions: results.assertions.filter(x => !x.pass).length,
    axeAudits: results.axe.length, axeViolations: results.axe.reduce((n, x) => n + x.violations.length, 0),
    axeIncompleteAudits: results.axe.filter(x => x.incomplete.length).length,
    screenshots: results.screenshots.length, externalRequests: results.externalRequests.length,
    consoleErrors: results.consoleErrors.length, brokenResponses: results.brokenResponses.length, errors: results.errors.length
  };
  results.pass = !Object.entries(results.summary).some(([k, n]) => /failed|Violations|externalRequests|consoleErrors|brokenResponses|^errors$/.test(k) && n);
  await fs.writeFile(path.join(evidence, smoke ? 'browser-smoke.json' : 'browser-results.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify({ pass: results.pass, ...results.summary, failed: results.states.filter(x => !x.pass).map(x => ({ route: x.route, width: x.width, mode: x.mode, outside: x.outside })), failedAssertionDetails: results.assertions.filter(x => !x.pass), errorDetails: results.errors }, null, 2));
  if (!results.pass) process.exitCode = 1;
}