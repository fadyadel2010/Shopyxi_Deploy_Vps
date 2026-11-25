// Phase 1A qualification: header + homepage hero at five widths in both locales.
// Review build only. No production artifact, deployment or screenshot mutation.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { pagePath, accessDestinations } from '../src/routes.mjs';
import { createPreview } from '../scripts/preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output/phase1a'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const require = createRequire(import.meta.url);
const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
const labels = JSON.parse(await fs.readFile(path.join(app, 'src/content/labels.json'), 'utf8'));
const widths = [1440, 1024, 768, 375, 320];
const locales = ['en', 'ar'];
const results = { startedUtc: new Date().toISOString(), origin: 'loopback preview', states: [], axe: [], assertions: [], errors: [], consoleErrors: [], externalRequests: [], brokenResponses: [], screenshots: [], fonts: [] };
const check = (pass, label, detail) => results.assertions.push({ pass: Boolean(pass), label, ...(detail === undefined ? {} : { detail }) });

const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
  const context = await browser.newContext({ serviceWorkers: 'block', deviceScaleFactor: 1 });
  await context.route('**/*', route => {
    if (route.request().url().startsWith(origin + '/')) return route.continue();
    results.externalRequests.push(route.request().url()); return route.abort();
  });
  const page = await context.newPage();
  page.on('pageerror', error => results.errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') results.consoleErrors.push(message.text()); });
  page.on('response', response => { if (response.status() >= 400) results.brokenResponses.push({ url: response.url(), status: response.status() }); });

  async function load(locale, width, text = 100) {
    await page.setViewportSize({ width, height: width < 600 ? 812 : 900 });
    const response = await page.goto(origin + pagePath(locale, 'home'), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    if (text !== 100) await page.evaluate(value => document.documentElement.style.setProperty('font-size', `${value}%`, 'important'), text);
    await page.evaluate(async () => {
      for (const image of document.images) { image.scrollIntoView(); await image.decode().catch(() => {}); }
      scrollTo(0, 0);
    });
    return response;
  }

  for (const locale of locales) {
    for (const width of widths) {
      const response = await load(locale, width);
      check(response.status() === 200, `HTTP 200 ${locale} ${width}`);
      const state = await page.evaluate(({ locale, width, expected }) => {
        const rect = el => { const b = el.getBoundingClientRect(); return { x: +b.x.toFixed(1), y: +b.y.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
        const visible = el => el.checkVisibility() && getComputedStyle(el).visibility !== 'hidden';
        const clientWidth = document.documentElement.clientWidth;
        const h1 = document.querySelector('main h1');
        const headerChildren = [...document.querySelector('.header-inner').children].filter(visible).map(el => ({ ...rect(el), name: el.className }));
        const tiles = [...document.querySelectorAll('.product-tile')].map(el => ({ ...rect(el), href: el.getAttribute('href'), name: el.querySelector('.product-tile-name')?.textContent }));
        const windows = [...document.querySelectorAll('.hero-window')].map(el => {
          const image = el.querySelector('img');
          const link = el.querySelector('.hero-window-frame');
          return { role: el.className, ...rect(el), src: image?.getAttribute('src'), naturalWidth: image?.naturalWidth,
            alt: image?.getAttribute('alt'), width: image?.getAttribute('width'), height: image?.getAttribute('height'),
            href: link?.getAttribute('href'), label: link?.getAttribute('aria-label'), loading: image?.getAttribute('loading'),
            naturalHeight: image?.naturalHeight,
            // Layout box (offsetWidth/Height), not the bounding box: the two rear
            // windows are rotated, which inflates their axis-aligned rect.
            boxRatio: image ? +(image.offsetWidth / image.offsetHeight).toFixed(4) : null,
            chromeName: el.querySelector('.window-name')?.textContent.trim(),
            chromeNameClipped: (() => { const n = el.querySelector('.window-name'); return n ? n.scrollWidth > n.clientWidth + 1 : false; })() };
        });
        const targets = [...document.querySelectorAll('.header a, .header summary, .hero-actions a, .product-tile, .hero-window-frame')]
          .filter(visible).map(el => ({ ...rect(el), text: (el.getAttribute('aria-label') || el.textContent).trim().slice(0, 40) }));
        const outside = [...document.querySelectorAll('body *')].filter(visible).filter(el => {
          if (el.classList.contains('skip') && el !== document.activeElement) return false;
          const b = el.getBoundingClientRect();
          return b.width > 0 && (b.left < -1 || b.right > clientWidth + 1);
        }).map(el => ({ tag: el.tagName, cls: el.getAttribute('class') }));
        return {
          locale: document.documentElement.lang, dir: document.documentElement.dir, width,
          scrollWidth: document.documentElement.scrollWidth, clientWidth,
          header: rect(document.querySelector('header.header')),
          headerRows: headerChildren.reduce((rows, child) => {
            const center = child.y + child.h / 2;
            return rows.some(row => Math.abs(row - center) < 24) ? rows : [...rows, center];
          }, []).length,
          headerChildren,
          h1: h1 ? { text: h1.innerText.replace(/\n+/g, ' | '), ...rect(h1), count: document.querySelectorAll('h1').length } : null,
          hero: rect(document.querySelector('.home-hero')),
          tiles, tileRows: new Set(tiles.map(tile => tile.y)).size,
          windows, outside,
          smallTargets: targets.filter(target => target.w < 44 || target.h < 44),
          primaryCta: (() => { const a = document.querySelector('.hero-cta-primary'); return a && { href: a.getAttribute('href'), text: a.textContent.trim() }; })(),
          secondaryCta: (() => { const a = document.querySelector('.hero-cta-secondary'); return a && { href: a.getAttribute('href'), text: a.textContent.trim() }; })(),
          languageHref: document.querySelector('#locale')?.getAttribute('href'),
          headerCta: (() => { const a = document.querySelector('.header-demo'); return a && { href: a.getAttribute('href'), text: a.textContent.trim() }; })(),
          navProducts: [...document.querySelectorAll('.nav-panel-link')].map(a => a.getAttribute('href')),
          navPlatform: document.querySelector('.nav-link')?.getAttribute('href'),
          ledger0: 0,
          ledger: Object.fromEntries(['pos', 'storefront', 'affiliate', 'commercehub'].map(key => [key,
            document.querySelectorAll(`.product-nav .tile-icon-${key}`).length + document.querySelectorAll(`.nav-panel .tile-icon-${key}`).length])) ,
          expected, reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
          tileTransition: getComputedStyle(document.querySelector('.product-tile')).transitionDuration
        };
      }, { locale, width, expected: labels[locale] });

      const prefix = `${locale}-${width}`;
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-fold.png`) });
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-full.png`), fullPage: true });
      results.screenshots.push(`${prefix}-fold.png`, `${prefix}-full.png`);

      check(state.locale === locale && state.dir === (locale === 'ar' ? 'rtl' : 'ltr'), `lang/dir ${prefix}`);
      check(state.scrollWidth <= state.clientWidth + 1, `No horizontal overflow ${prefix}`, { scrollWidth: state.scrollWidth });
      check(state.outside.length === 0, `No element outside viewport ${prefix}`, state.outside.slice(0, 6));
      check(state.h1?.count === 1, `Single h1 ${prefix}`);
      check(state.h1?.text.includes(labels[locale].heroline1) && state.h1?.text.includes(labels[locale].heroline2), `Approved headline ${prefix}`, state.h1?.text);
      check(state.header.h <= (width <= 400 ? 100 : width < 768 ? 72 : width < 1200 ? 78 : 84), `Header height ${prefix}`, state.header.h);
      check(state.headerRows === 1, `Single header row at default text size ${prefix}`, state.headerChildren);
      check(state.windows.length === 3, `Three genuine application windows ${prefix}`);
      check(state.windows.every(window => window.naturalWidth > 0 && window.alt && window.width && window.height), `Windows loaded with alt and dimensions ${prefix}`, state.windows.map(w => ({ src: w.src, nw: w.naturalWidth })));
      check(state.windows.every(window => window.src.includes(`-${locale}-`) && window.href === window.src), `Windows use ${locale} capture and link to it ${prefix}`);
      check(state.windows.filter(window => window.loading === 'eager').length === 1 && /hero-window hero-window-pos/.test(state.windows.find(window => window.loading === 'eager')?.role || ''), `Only the foreground POS capture is prioritised ${prefix}`, state.windows.map(w => [w.role, w.loading]));
      check(state.windows.every(window => window.naturalWidth && window.naturalHeight && Math.abs(window.boxRatio - window.naturalWidth / window.naturalHeight) < 0.01), `Captures keep their exact aspect ratio (no crop) ${prefix}`, state.windows.map(w => [w.chromeName, w.boxRatio, +(w.naturalWidth / w.naturalHeight).toFixed(4)]));
      check(state.windows.every(window => window.chromeName && !window.chromeNameClipped), `Window chrome names are complete, never truncated ${prefix}`, state.windows.map(w => [w.chromeName, w.chromeNameClipped]));
      check(state.windows.some(window => /hero-window-pos/.test(window.role)) && Math.max(...state.windows.map(window => window.w)) === state.windows.find(window => /hero-window-pos/.test(window.role)).w, `POS window is the largest ${prefix}`);
      check(state.tiles.length === 4, `Four product tiles ${prefix}`);
      check(state.tiles.map(tile => tile.name).join(',') === ['POS Admin', 'Storefront', 'Affiliate System', 'CommerceHub'].join(','), `Tile order POS→CommerceHub in DOM ${prefix}`, state.tiles.map(t => t.name));
      check(locale === 'ar'
        ? state.tiles[0].x > state.tiles[3].x
        : state.tiles[0].x < state.tiles[3].x, `Tile start edge follows writing direction ${prefix}`, state.tiles.map(t => [t.name, t.x]));
      check(state.tileRows === (width >= 1024 ? 1 : 2), `Tile rows ${prefix}`, state.tileRows);
      check(state.tiles.every(tile => tile.href === `${pagePath(locale, ['pos', 'storefront', 'affiliate', 'commercehub'][state.tiles.indexOf(tile)])}#main`), `Tile destinations ${prefix}`, state.tiles.map(t => t.href));
      check(state.primaryCta?.href === '#products' && state.primaryCta.text === labels[locale].exploreproducts, `Primary CTA ${prefix}`, state.primaryCta);
      check(state.secondaryCta?.href === `${pagePath(locale, 'platform')}#main`, `Secondary CTA ${prefix}`, state.secondaryCta);
      check(state.languageHref === `${pagePath(locale === 'en' ? 'ar' : 'en', 'home')}#locale`, `Language switch preserves context ${prefix}`, state.languageHref);
      check(state.navProducts.length === 4 && state.navProducts.every(href => href.startsWith(`/${locale}/products/`)), `Mega menu exposes four product routes ${prefix}`, state.navProducts);
      check(state.navPlatform === `${pagePath(locale, 'platform')}#main`, `Platform destination unchanged ${prefix}`, state.navPlatform);
      check(state.smallTargets.length === 0, `All interactive targets ≥44px ${prefix}`, state.smallTargets);
      check(Object.values(state.ledger).every(count => count === 2), `Product glyph visible in tiles and mega menu ${prefix}`, state.ledger);
      results.states.push({ ...state, prefix });

      await page.emulateMedia({ reducedMotion: 'reduce' });
      const reduced = await page.evaluate(() => getComputedStyle(document.querySelector('.product-tile')).transitionDuration);
      check(reduced === '0s', `Reduced motion disables tile transition ${prefix}`, reduced);
      await page.emulateMedia({ reducedMotion: 'no-preference' });

      // Keyboard: products menu at desktop/tablet-landscape, drawer below it.
      // The branch follows the rendered navigation, not a hard-coded width.
      const desktopNav = await page.locator('.nav').isVisible();
      if (desktopNav) {
        const summary = page.locator('.nav-menu > summary');
        await summary.focus(); await page.keyboard.press('Enter');
        check(await page.locator('.nav-menu').getAttribute('open') !== null, `Products menu opens with keyboard ${prefix}`);
        check(await page.evaluate(() => document.activeElement?.tagName) !== 'BODY', `Focus retained after open ${prefix}`);
        const firstLink = page.locator('.nav-panel-link').first();
        await firstLink.focus();
        check(await page.evaluate(() => document.activeElement?.getAttribute('href')).then(href => href?.startsWith(`/${locale}/products/`)), `Mega menu link focusable ${prefix}`);
        await page.keyboard.press('Escape');
        check(await page.locator('.nav-menu').getAttribute('open') === null, `Escape closes products menu ${prefix}`);
        check(await summary.evaluate(el => el === document.activeElement), `Escape restores focus to trigger ${prefix}`);
      } else {
        const mobile = page.locator('.mobile-nav > summary');
        await mobile.focus(); await page.keyboard.press('Enter');
        check(await page.locator('.mobile-nav').getAttribute('open') !== null, `Mobile menu opens with keyboard ${prefix}`);
        const panel = await page.locator('.mobile-panel').boundingBox();
        const viewport = page.viewportSize();
        check(panel.x >= 0 && panel.x + panel.width <= viewport.width, `Mobile menu stays inside viewport ${prefix}`, panel);
        await page.keyboard.press('Escape');
        check(await page.locator('.mobile-nav').getAttribute('open') === null && await mobile.evaluate(el => el === document.activeElement), `Escape closes mobile menu and restores focus ${prefix}`);
      }
      const access = page.locator('#customer-access > summary');
      await access.focus(); await page.keyboard.press('Enter');
      check(await page.locator('#customer-access').getAttribute('open') !== null, `Customer login opens ${prefix}`);
      const panelBox = await page.locator('#customer-access .access-panel').boundingBox();
      const view = page.viewportSize();
      check(panelBox.x >= -1 && panelBox.x + panelBox.width <= view.width + 1, `Login panel stays inside viewport ${prefix}`, panelBox);
      const hrefs = await page.locator('#customer-access .access-panel a').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')));
      check(JSON.stringify(hrefs) === JSON.stringify(accessDestinations), `Login destinations unchanged ${prefix}`, hrefs);
      const targetSizes = await page.locator('#customer-access .access-panel a').evaluateAll(nodes => nodes.map(node => { const b = node.getBoundingClientRect(); return [b.width, b.height]; }));
      check(targetSizes.every(([, height]) => height >= 44), `Login links ≥44px high ${prefix}`, targetSizes);
      await page.keyboard.press('Escape');
      check(await page.locator('#customer-access').getAttribute('open') === null && await access.evaluate(el => el === document.activeElement), `Escape closes login and restores focus ${prefix}`);

      await page.evaluate(axeSource);
      const audit = await page.evaluate(async () => {
        const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice'] } });
        return { violations: result.violations, incomplete: result.incomplete, passes: result.passes.length };
      });
      results.axe.push({ locale, width, ...audit });
      check(audit.violations.length === 0, `axe WCAG 2.0/2.1/2.2 A/AA violations 0 ${prefix}`, audit.violations.map(v => v.id));
      check(audit.incomplete.length === 0, `axe incomplete 0 ${prefix}`, audit.incomplete.map(v => v.id));

      // 200% root text reflow.
      await load(locale, width, 200);
      const zoomed = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
        outside: [...document.querySelectorAll('body *')].filter(el => el.checkVisibility()).filter(el => {
          if (el.classList.contains('skip') && el !== document.activeElement) return false;
          const b = el.getBoundingClientRect();
          return b.width > 0 && (b.left < -1 || b.right > document.documentElement.clientWidth + 1);
        }).length
      }));
      check(zoomed.scrollWidth <= zoomed.clientWidth + 1 && zoomed.outside === 0, `200% text scaling reflows without overflow ${prefix}`, zoomed);
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-text200-fold.png`) });
      results.screenshots.push(`${prefix}-text200-fold.png`);
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-text200-full.png`), fullPage: true });
      results.screenshots.push(`${prefix}-text200-full.png`);
    }
  }

  // Actual rendered font check on the new hero typography.
  for (const locale of locales) {
    await load(locale, 1440);
    const session = await context.newCDPSession(page);
    await session.send('DOM.enable'); await session.send('CSS.enable');
    const document_ = await session.send('DOM.getDocument');
    for (const selector of ['main h1', '.hero-copy > p', '.product-tile-name', '.window-name', '.hero-eyebrow bdi']) {
      const { nodeId } = await session.send('DOM.querySelector', { nodeId: document_.root.nodeId, selector });
      const fonts = await session.send('CSS.getPlatformFontsForNode', { nodeId });
      results.fonts.push({ locale, selector, fonts: fonts.fonts });
      check(fonts.fonts.length > 0 && fonts.fonts.every(font => font.familyName === 'Cairo' && font.isCustomFont), `Rendered Cairo ${locale} ${selector}`, fonts.fonts);
    }
    await session.detach();
  }
  await context.close();
} catch (error) {
  results.errors.push(error.stack);
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
results.summary = {
  states: results.states.length,
  assertions: results.assertions.length,
  failedAssertions: results.assertions.filter(item => !item.pass).length,
  axeAudits: results.axe.length,
  axeViolations: results.axe.reduce((total, item) => total + item.violations.length, 0),
  axeIncomplete: results.axe.filter(item => item.incomplete.length).length,
  consoleErrors: results.consoleErrors.length,
  externalRequests: results.externalRequests.length,
  brokenResponses: results.brokenResponses.length,
  errors: results.errors.length,
  screenshots: results.screenshots.length
};
results.pass = results.states.length === locales.length * widths.length && results.summary.failedAssertions === 0 &&
  results.summary.axeViolations === 0 && results.summary.axeIncomplete === 0 && results.summary.consoleErrors === 0 &&
  results.summary.externalRequests === 0 && results.summary.brokenResponses === 0 && results.summary.errors === 0;
results.completedUtc = new Date().toISOString();
await fs.writeFile(path.join(evidence, 'phase1a-browser-results.json'), JSON.stringify(results, null, 2) + '\n');
console.log(JSON.stringify({ pass: results.pass, summary: results.summary, failures: results.assertions.filter(item => !item.pass) }, null, 2));
if (!results.pass) process.exitCode = 1;
