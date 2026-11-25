// Phase 1B qualification: Product Showcase, both locales, five widths.
// Review build only. No production artifact, deployment or screenshot mutation.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { pagePath } from '../src/routes.mjs';
import { createPreview } from '../scripts/preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'temp/phase1b-test'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const require = createRequire(import.meta.url);
const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
const labels = JSON.parse(await fs.readFile(path.join(app, 'src/content/labels.json'), 'utf8'));
const claims = JSON.parse(await fs.readFile(path.join(app, 'src/content/claims.json'), 'utf8'));
const screenshots = JSON.parse(await fs.readFile(path.join(app, 'src/content/product-screenshots.json'), 'utf8'));
const productKeys = ['pos', 'storefront', 'affiliate', 'commercehub'];
const widths = [1440, 1024, 768, 375, 320];
const locales = ['en', 'ar'];
const results = { startedUtc: new Date().toISOString(), origin: 'loopback preview', states: [], axe: [], assertions: [], screenshots: [], errors: [], consoleErrors: [], externalRequests: [], brokenResponses: [] };
const check = (pass, label, detail) => results.assertions.push({ pass: Boolean(pass), label, ...(detail === undefined ? {} : { detail }) });
const server = createPreview();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
let page;
try {
  browser = await chromium.launch({ headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-pings'] });
  const context = await browser.newContext({ serviceWorkers: 'block', deviceScaleFactor: 1 });
  await context.route('**/*', route => {
    if (route.request().url().startsWith(origin + '/')) return route.continue();
    results.externalRequests.push(route.request().url()); return route.abort();
  });
  page = await context.newPage();
  page.on('pageerror', error => results.errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') results.consoleErrors.push(message.text()); });
  page.on('response', response => { if (response.status() >= 400) results.brokenResponses.push({ url: response.url(), status: response.status() }); });

  async function load(locale, width, text = 100) {
    await page.setViewportSize({ width, height: width < 600 ? 812 : 900 });
    const response = await page.goto(origin + pagePath(locale, 'home'), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    if (text !== 100) await page.evaluate(value => document.documentElement.style.setProperty('font-size', `${value}%`, 'important'), text);
    await page.evaluate(async () => {
      for (const image of document.querySelectorAll('.ps-section img')) { image.scrollIntoView({ block: 'center' }); await image.decode().catch(() => {}); }
      document.querySelector('.ps-section').scrollIntoView({ block: 'start' });
    });
    return response;
  }

  for (const locale of locales) {
    for (const width of widths) {
      const response = await load(locale, width);
      check(response.status() === 200, `HTTP 200 ${locale} ${width}`);
      const state = await page.evaluate(({ locale, width, expected, labels, claims, screenshots }) => {
        const rect = el => { const b = el.getBoundingClientRect(); return { x: +b.x.toFixed(1), y: +b.y.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
        const visible = el => el.checkVisibility() && getComputedStyle(el).visibility !== 'hidden';
        const clientWidth = document.documentElement.clientWidth;
        const outside = [...document.querySelectorAll('body *')].filter(visible).filter(el => {
          if (el.classList.contains('skip') && el !== document.activeElement) return false;
          const b = el.getBoundingClientRect();
          return b.width > 0 && (b.left < -1 || b.right > clientWidth + 1);
        }).map(el => ({ tag: el.tagName, cls: el.getAttribute('class'), text: el.textContent.slice(0, 70) }));
        const section = document.querySelector('.ps-section');
        const cards = [...section.querySelectorAll('.ps-card')];
        const productKeys = ['pos', 'storefront', 'affiliate', 'commercehub'];
        const cardData = cards.map((card, index) => {
          const link = card.querySelector('a[href]');
          const figure = card.querySelector('figure');
          const image = card.querySelector('img');
          const description = card.querySelector('.ps-desc');
          const media = card.querySelector('.ps-media');
          const copy = card.querySelector('.ps-copy');
          const action = card.querySelector('.ps-action');
          const manifest = screenshots.products[productKeys[index]][locale].desktop;
          const descId = link?.getAttribute('aria-describedby');
          const desc = descId ? document.getElementById(descId) : null;
          const naturalRatio = image && image.naturalHeight ? image.naturalWidth / image.naturalHeight : 0;
          const display = image ? rect(image) : null;
          const copyRect = copy ? rect(copy) : null;
          const mediaRect = media ? rect(media) : null;
          const sideBySide = Boolean(copyRect && mediaRect && (locale === 'ar' ? copyRect.x >= mediaRect.x + mediaRect.w - 2 : copyRect.x + copyRect.w <= mediaRect.x + 2));
          return {
            index,
            card: rect(card),
            link: link ? { ...rect(link), text: link.innerText.trim(), href: link.getAttribute('href'), describedBy: descId, descText: desc?.textContent, outline: getComputedStyle(link).outlineStyle, outlineWidth: parseFloat(getComputedStyle(link).outlineWidth) } : null,
            linkCount: card.querySelectorAll('a').length,
            title: card.querySelector('h3')?.textContent.trim(),
            description: description?.textContent,
            media: mediaRect,
            copy: copyRect,
            sideBySide,
            figure: figure ? { product: figure.dataset.screenProduct, candidate: figure.dataset.screenCandidate, variant: figure.dataset.screenVariant } : null,
            image: image ? { src: image.getAttribute('src'), width: image.getAttribute('width'), height: image.getAttribute('height'), alt: image.getAttribute('alt'), loading: image.getAttribute('loading'), naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight, complete: image.complete, display, naturalRatio } : null,
            manifest: { publicPath: manifest.publicPath, sha: manifest.publicSha256 },
            action: action ? { visible: getComputedStyle(action).visibility !== 'hidden', ariaHidden: action.getAttribute('aria-hidden'), text: action.textContent.trim(), rect: rect(action), bg: getComputedStyle(action).backgroundColor } : null
          };
        });
        const grid = section.querySelector('.ps-grid');
        return {
          locale: document.documentElement.lang, dir: document.documentElement.dir,
          width, clientWidth, scrollWidth: document.documentElement.scrollWidth,
          outside,
          scrollWidthInside: [...document.querySelectorAll('.ps-section *')].filter(el => el.scrollWidth > el.clientWidth + 1).map(el => ({ tag: el.tagName, cls: el.getAttribute('class'), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth })),
          section: rect(section), grid: rect(grid), gridCols: getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length,
          heading: section.querySelector('h2')?.textContent,
          headingId: section.querySelector('h2')?.id,
          labelledBy: section.getAttribute('aria-labelledby'),
          tabIndex: section.tabIndex,
          eyebrow: section.querySelector('.ps-eyebrow')?.textContent,
          sub: section.querySelector('.ps-sub')?.textContent,
          disclosure: section.querySelector('.screen-disclosure')?.textContent,
          cards: cardData,
          h1s: document.querySelectorAll('h1').length,
          headingLevels: [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(e => +e.tagName[1]),
          duplicateIds: [...document.querySelectorAll('[id]')].map(e => e.id).filter((id, i, all) => all.indexOf(id) !== i),
          oldSection: document.querySelectorAll('.introductions, .scope').length,
          lowerSections: ['relationships', 'capabilities', 'proof', 'faq', 'demo'].filter(id => !document.getElementById(id)),
          boundaries: ['C06', 'C31', 'C35'].filter(id => !document.querySelector(`body [data-claim="${id}"]`)),
          expected: labels[locale], c01: claims.C01[locale],
          live: { localStorage: localStorage.length, sessionStorage: sessionStorage.length, cookies: document.cookie }
        };
      }, { locale, width, expected: labels[locale], labels, claims, screenshots });

      const prefix = `${locale}-${width}`;
      check(state.locale === locale && state.dir === (locale === 'ar' ? 'rtl' : 'ltr'), `lang/dir ${prefix}`);
      check(state.scrollWidth <= state.clientWidth + 1, `No page horizontal overflow ${prefix}`, { scrollWidth: state.scrollWidth });
      check(state.outside.length === 0, `No element outside viewport ${prefix}`, state.outside.slice(0, 8));
      check(state.scrollWidthInside.length === 0, `No internal horizontal overflow ${prefix}`, state.scrollWidthInside.slice(0, 8));
      check(state.section && state.section.h > 200, `Showcase section rendered ${prefix}`);
      check(state.heading === labels[locale].showcaseheading, `Approved heading ${prefix}`, state.heading);
      check(state.eyebrow === labels[locale].showcaseeyebrow, `Approved eyebrow ${prefix}`, state.eyebrow);
      const c01Terms = locale === 'en'
        ? ['merchant operations', 'online storefronts', 'affiliate promotion', 'marketplace selling']
        : ['أعمال التجار', 'المتاجر الإلكترونية', 'التسويق بالعمولة', 'السوق الإلكتروني'];
      const forbiddenClaims = locale === 'en'
        ? ['fully integrated', 'automatic', 'sync', 'all in one', 'seamless']
        : ['متكامل', 'تلقائي', 'مزامنة'];
      check(state.sub === labels[locale].showcasesub, `Approved supporting copy ${prefix}`, state.sub);
      check(c01Terms.every(term => state.sub.includes(term)), `Supporting copy carries C01 scope ${prefix}`, state.sub);
      check(!forbiddenClaims.some(term => state.sub.toLowerCase().includes(term.toLowerCase())), `No universal integration/sync claim ${prefix}`, state.sub);
      check(state.disclosure && state.disclosure.length > 20, `Synthetic-data disclosure present ${prefix}`);
      check(state.labelledBy === state.headingId && state.tabIndex === -1, `Section named and focusable for #products ${prefix}`);
      check(state.h1s === 1, `Single h1 ${prefix}`);
      check(!state.headingLevels.some((n, i) => i && n > state.headingLevels[i - 1] + 1), `No heading skips ${prefix}`, state.headingLevels);
      check(state.duplicateIds.length === 0, `No duplicate ids ${prefix}`, state.duplicateIds);
      check(state.oldSection === 0, `Obsolete Scope/Introductions absent from home ${prefix}`);
      check(state.lowerSections.length === 0, `Lower home sections preserved ${prefix}`, state.lowerSections);
      check(state.boundaries.length === 0, `Home claim boundaries preserved ${prefix}`, state.boundaries);
      check(state.cards.length === 4, `Four cards ${prefix}`);
      check(state.gridCols === (width >= 1440 ? 2 : 1), `Grid columns ${prefix}`, state.gridCols);
      check(JSON.stringify(state.cards.map(c => c.title)) === JSON.stringify(productKeys.map(key => labels[locale][key])), `DOM order POS→CommerceHub ${prefix}`, state.cards.map(c => c.title));
      check(state.cards.every((c, i) => c.image && c.image.src.endsWith(c.manifest.publicPath)), `Manifest capture used ${prefix}`, state.cards.map(c => c.image?.src));
      check(state.cards.every((c, i) => c.image && c.image.alt === screenshots.products[productKeys[i]][locale].alt), `Locale-specific alt ${prefix}`);
      check(state.cards.every(c => c.image && c.image.width && c.image.height && c.image.complete && c.image.naturalWidth > 0), `Capture loaded with dimensions ${prefix}`);
      check(state.cards.every(c => c.image && c.image.loading === 'lazy'), `Captures lazy-loaded ${prefix}`, state.cards.map(c => c.image?.loading));
      check(state.cards.every(c => c.image && Math.abs(c.image.display.w / c.image.display.h - c.image.naturalRatio) < 0.01), `Capture aspect ratio preserved (no crop) ${prefix}`);
      check(state.cards.every(c => c.figure && c.figure.product && c.figure.variant === 'desktop'), `Genuine desktop candidate metadata ${prefix}`);
      check(state.cards.every(c => c.linkCount === 1), `Exactly one link per card ${prefix}`, state.cards.map(c => c.linkCount));
      check(state.cards.every((c, i) => c.link && c.link.text === labels[locale][productKeys[i]]), `Card link name is product name only ${prefix}`, state.cards.map(c => c.link?.text));
      check(state.cards.every((c, i) => c.link && c.link.href === `${pagePath(locale, productKeys[i])}#main`), `Card destinations ${prefix}`, state.cards.map(c => c.link?.href));
      check(state.cards.every((c, i) => c.link && c.link.describedBy && c.link.descText === labels[locale][`show${productKeys[i]}`]), `Description connected through aria-describedby ${prefix}`);
      check(state.cards.every(c => c.action && c.action.ariaHidden === 'true' && c.action.text), `CTA is visual affordance only, not a second link ${prefix}`);
      check(state.cards.every(c => c.link && c.link.w >= 43.9 && c.link.h >= 43.9), `Card link target ≥44px ${prefix}`, state.cards.map(c => [c.link?.w, c.link?.h]));
      const focusProbe = await page.evaluate(() => {
        const link = document.querySelector('.ps-card-link');
        link.focus();
        const style = getComputedStyle(link);
        const result = { outlineStyle: style.outlineStyle, outlineWidth: parseFloat(style.outlineWidth), outlineColor: style.outlineColor };
        link.blur();
        return result;
      });
      check(focusProbe.outlineStyle !== 'none' && focusProbe.outlineWidth >= 2, `Visible focus outline when focused ${prefix}`, focusProbe);
      check(state.live.localStorage === 0 && state.live.sessionStorage === 0 && state.live.cookies === '', `No client storage ${prefix}`);

      if (width >= 1440) {
        check(state.cards[0].card.y === state.cards[1].card.y && state.cards[2].card.y === state.cards[3].card.y && state.cards[0].card.y < state.cards[2].card.y, `2x2 desktop rows ${prefix}`, state.cards.map(c => c.card.y));
        check(locale === 'ar'
          ? state.cards[0].card.x > state.cards[1].card.x && state.cards[2].card.x > state.cards[3].card.x
          : state.cards[0].card.x < state.cards[1].card.x && state.cards[2].card.x < state.cards[3].card.x, `Row order follows reading direction ${prefix}`, state.cards.map(c => c.card.x));
      } else {
        check(state.cards.every((c, i) => i === 0 || c.card.y > state.cards[i - 1].card.y), `Single-column card flow ${prefix}`, state.cards.map(c => c.card.y));
      }
      if (width >= 1024) {
        check(state.cards.every(c => c.sideBySide), `Horizontal copy/media at ${prefix}`);
        check(state.cards.every(c => locale === 'ar' ? c.copy.x > c.media.x : c.copy.x < c.media.x), `Copy on reading-start side ${prefix}`, state.cards.map(c => [c.copy?.x, c.media?.x]));
      } else {
        check(state.cards.every(c => !c.sideBySide), `Stacked copy/media at ${prefix}`);
        check(state.cards.every(c => c.media.y < c.copy.y), `Media above copy when stacked ${prefix}`);
      }
      check(state.cards.every(c => c.action && c.action.rect.h >= 43.9), `CTA visual target tall enough ${prefix}`);

      await page.evaluate(axeSource);
      const audit = await page.evaluate(async () => {
        const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice'] } });
        return { violations: result.violations, incomplete: result.incomplete, passes: result.passes.length };
      });
      results.axe.push({ locale, width, ...audit });
      check(audit.violations.length === 0, `axe violations 0 ${prefix}`, audit.violations.map(v => v.id));
      check(audit.incomplete.length === 0, `axe incomplete 0 ${prefix}`, audit.incomplete.map(v => v.id));
      results.states.push({ ...state, prefix });

      await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-section.png`) });
      if (width === 1440) {
        await page.locator('.ps-card').first().screenshot({ path: path.join(evidence, 'screenshots', `${locale}-1440-card-focus-base.png`) });
      }
    }
  }

  // Focus and hover states at desktop, with real keyboard focus.
  await load('en', 1440);
  const firstLink = page.locator('.ps-card-link').first();
  await firstLink.focus();
  const focusState = await page.evaluate(() => {
    const link = document.activeElement;
    const card = link.closest('.ps-card');
    const action = card.querySelector('.ps-action');
    return { tag: link.tagName, outline: getComputedStyle(link).outline, cardBorder: getComputedStyle(card).borderColor, actionBg: getComputedStyle(action).backgroundColor, cardTransform: getComputedStyle(card).transform };
  });
  check(focusState.tag === 'A' && focusState.outline.includes('3px') && parseFloat(focusState.outline.split(' ')[0]) >= 2, 'Keyboard focus style is real', focusState);
  await page.locator('.ps-card').first().screenshot({ path: path.join(evidence, 'screenshots', 'en-1440-card-focus.png') });
  results.screenshots.push('en-1440-card-focus.png');
  await page.locator('.ps-card').first().hover();
  await page.waitForTimeout(350);
  const hoverState = await page.evaluate(() => {
    const card = document.querySelector('.ps-card');
    return { transform: getComputedStyle(card).transform, border: getComputedStyle(card).borderColor, actionBg: getComputedStyle(card.querySelector('.ps-action')).backgroundColor };
  });
  check(hoverState.transform !== 'none', 'Hover lift is applied', hoverState);
  await page.locator('.ps-card').first().screenshot({ path: path.join(evidence, 'screenshots', 'en-1440-card-hover.png') });
  results.screenshots.push('en-1440-card-hover.png');

  await page.emulateMedia({ reducedMotion: 'reduce' });
  await load('en', 1440);
  await page.locator('.ps-card').first().hover();
  await page.waitForTimeout(100);
  const reduced = await page.evaluate(() => {
    const card = document.querySelector('.ps-card');
    return { transform: getComputedStyle(card).transform, duration: getComputedStyle(card).transitionDuration };
  });
  check(reduced.transform === 'none' && reduced.duration.split(',').every(value => parseFloat(value) === 0), 'Reduced motion disables lift/transition', reduced);
  await page.emulateMedia({ reducedMotion: 'no-preference' });

  // #products anchor focuses the section and retains the URL hash.
  await load('en', 1440);
  await page.evaluate(() => scrollTo(0, 0));
  await page.locator('main a[href="#products"]').first().click();
  await page.waitForTimeout(250);
  check(new URL(page.url()).hash === '#products' && await page.evaluate(() => document.activeElement?.id) === 'products', 'Hero #products anchor focuses showcase section');

  // 200% root text reflow, both locales at desktop and mobile.
  for (const locale of locales) {
    for (const width of [1440, 375]) {
      await load(locale, width, 200);
      const zoom = await page.evaluate(() => {
        const clientWidth = document.documentElement.clientWidth;
        const outside = [...document.querySelectorAll('body *')].filter(el => el.checkVisibility()).filter(el => {
          if (el.classList.contains('skip') && el !== document.activeElement) return false;
          const b = el.getBoundingClientRect();
          return b.width > 0 && (b.left < -1 || b.right > clientWidth + 1);
        }).map(el => ({ tag: el.tagName, cls: el.getAttribute('class'), text: el.textContent.slice(0, 50) }));
        const cards = [...document.querySelectorAll('.ps-card')];
        return { clientWidth, scrollWidth: document.documentElement.scrollWidth, outside, rootFont: getComputedStyle(document.documentElement).fontSize,
          cardWidths: cards.map(c => c.getBoundingClientRect().width), linkSizes: cards.map(c => { const r = c.querySelector('.ps-card-link').getBoundingClientRect(); return [r.width, r.height]; }),
          cardOverflow: cards.filter(c => c.scrollWidth > c.clientWidth + 1).length };
      });
      const p = `${locale}-${width}-text200`;
      check(zoom.scrollWidth <= zoom.clientWidth + 1 && zoom.outside.length === 0, `200% text reflow without overflow ${p}`, zoom);
      check(zoom.linkSizes.every(([w, h]) => w >= 43.9 && h >= 43.9), `200% card links remain targetable ${p}`, zoom.linkSizes);
      await page.screenshot({ path: path.join(evidence, 'screenshots', `${p}-viewport.png`) });
      await page.locator('.ps-card').first().screenshot({ path: path.join(evidence, 'screenshots', `${p}-first-card.png`) });
      results.screenshots.push(`${p}-viewport.png`, `${p}-first-card.png`);
    }
  }

  await context.close();
} catch (error) {
  results.errors.push(error.stack);
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png'), fullPage: false }).catch(() => {});
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
  axeIncompleteAudits: results.axe.filter(item => item.incomplete.length).length,
  consoleErrors: results.consoleErrors.length,
  externalRequests: results.externalRequests.length,
  brokenResponses: results.brokenResponses.length,
  errors: results.errors.length,
  screenshots: results.screenshots.length
};
results.pass = results.states.length === locales.length * widths.length && results.summary.failedAssertions === 0 && results.summary.axeViolations === 0 && results.summary.axeIncompleteAudits === 0 && results.summary.consoleErrors === 0 && results.summary.externalRequests === 0 && results.summary.brokenResponses === 0 && results.summary.errors === 0;
results.completedUtc = new Date().toISOString();
await fs.writeFile(path.join(evidence, 'phase1b-browser-results.json'), JSON.stringify(results, null, 2) + String.fromCharCode(10));
console.log(JSON.stringify({ pass: results.pass, summary: results.summary, failures: results.assertions.filter(item => !item.pass).slice(0, 20), errorDetails: results.errors }, null, 2));
if (!results.pass) process.exitCode = 1;
