import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const EVIDENCE = process.env.PHASE1D_EVIDENCE
  ? path.resolve(ROOT, '..', process.env.PHASE1D_EVIDENCE)
  : path.resolve(ROOT, '..', 'docs', 'marketing', 'homepage-redesign-phase-1d-capabilities-20261124');
const BASE = process.env.PHASE1D_BASE || 'http://127.0.0.1:4192';
const BASELINE = process.env.PHASE1D_BASELINE || 'http://127.0.0.1:4193';
const require = createRequire(import.meta.url);
const AXE_SOURCE = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
const MANIFEST = JSON.parse(fs.readFileSync(path.join(ROOT, 'src', 'content', 'product-screenshots.json'), 'utf8'));
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];
const WIDTHS = [1440, 1024, 768, 375, 320];
const LOCALES = ['en', 'ar'];
fs.mkdirSync(EVIDENCE, { recursive: true });

const report = {
  generatedAt: new Date().toISOString(),
  base: BASE,
  widths: WIDTHS,
  locales: LOCALES,
  checks: [],
  failures: [],
  warnings: [],
  responsive: {},
  axe: {},
  axeBaseline: {},
  fullPageAxe: {},
  fullPageAxeBaseline: {},
  links: {},
  keyboard: {},
  reducedMotion: {},
  textScale: {},
  images: {},
  console: {}
};

function check(name, ok, detail) {
  const entry = { name, ok: Boolean(ok), detail: detail === undefined ? null : detail };
  report.checks.push(entry);
  if (!ok) report.failures.push(entry);
  return ok;
}
function warn(name, detail) { report.warnings.push({ name, detail }); }
function rect(r) {
  return { x: Math.round(r.x * 100) / 100, y: Math.round(r.y * 100) / 100, width: Math.round(r.width * 100) / 100, height: Math.round(r.height * 100) / 100, right: Math.round(r.right * 100) / 100, bottom: Math.round(r.bottom * 100) / 100 };
}
function slug(value) { return String(value).replace(/[^a-z0-9_-]+/gi, '-').toLowerCase(); }
function slimAxe(result) {
  const simplify = list => list.map(item => ({
    id: item.id, impact: item.impact, help: item.help,
    nodes: item.nodes.map(node => ({ target: node.target, html: node.html, failureSummary: node.failureSummary }))
  }));
  return { violations: simplify(result.violations), incomplete: simplify(result.incomplete), passes: result.passes.length };
}

async function load(page, locale) {
  await page.goto(BASE + '/' + locale + '/', { waitUntil: 'networkidle' });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(180);
}

async function warmImages(page) {
  await page.evaluate(async () => {
    const section = document.querySelector('#capabilities');
    const top = section.offsetTop;
    const end = top + section.offsetHeight;
    const step = Math.max(240, Math.round(window.innerHeight * 0.72));
    for (let y = top; y < end; y += step) {
      window.scrollTo(0, y);
      await new Promise(resolve => setTimeout(resolve, 55));
    }
    window.scrollTo(0, top);
  });
  await page.waitForFunction(() => [...document.querySelectorAll('#capabilities img')].every(img => img.complete && img.naturalWidth > 0), null, { timeout: 15000 });
  await page.waitForTimeout(140);
}

async function collect(page, locale, width) {
  return page.evaluate(({ locale, width }) => {
    const section = document.querySelector('#capabilities');
    if (!section) return { error: 'missing #capabilities' };
    const vw = document.documentElement.clientWidth;
    const doc = document.documentElement;
    const cs = getComputedStyle(section);
    const q = (root, selector) => root.querySelector(selector);
    const box = el => { const r = el.getBoundingClientRect(); return { x: Math.round(r.x * 100) / 100, y: Math.round(r.y * 100) / 100, width: Math.round(r.width * 100) / 100, height: Math.round(r.height * 100) / 100, right: Math.round(r.right * 100) / 100, bottom: Math.round(r.bottom * 100) / 100, left: Math.round(r.left * 100) / 100 }; };
    const visible = el => { if (!el) return false; const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden'; };
    const textOverflow = [...section.querySelectorAll('h2,h3,p,a,span')].filter(el => {
      const s = getComputedStyle(el);
      return visible(el) && el.scrollWidth > el.clientWidth + 1 && s.overflow !== 'hidden' && s.textOverflow !== 'ellipsis';
    }).map(el => ({ tag: el.tagName, cls: String(el.className).slice(0, 70), text: (el.textContent || '').trim().slice(0, 90), clientWidth: el.clientWidth, scrollWidth: el.scrollWidth }));
    const panels = [...section.querySelectorAll('[data-bc-panel]')].map(panel => {
      const copy = q(panel, '.bc-copy');
      const visual = q(panel, '.bc-visual');
      const heading = q(panel, 'h3');
      const desc = q(panel, '.bc-desc');
      const actions = q(panel, '.bc-actions');
      const ctas = [...panel.querySelectorAll('.bc-cta')].map(cta => ({
        text: (cta.textContent || '').trim(),
        href: cta.getAttribute('href'),
        resolvedPath: new URL(cta.href).pathname + new URL(cta.href).hash,
        ariaLabel: cta.getAttribute('aria-label'),
        rect: box(cta),
        top: Math.round(cta.getBoundingClientRect().top * 100) / 100,
        visible: visible(cta),
        minSide: Math.min(cta.getBoundingClientRect().width, cta.getBoundingClientRect().height),
        background: getComputedStyle(cta).backgroundColor,
        color: getComputedStyle(cta).color,
        fontSize: getComputedStyle(cta).fontSize,
        fontWeight: getComputedStyle(cta).fontWeight,
        textClipped: cta.scrollWidth > cta.clientWidth + 1
      }));
      const images = [...panel.querySelectorAll('img')].map(img => {
        const s = getComputedStyle(img);
        const shot = img.closest('.bc-shot');
        return {
          src: img.getAttribute('src'),
          alt: img.getAttribute('alt'),
          complete: img.complete,
          naturalWidth: img.naturalWidth,
          naturalHeight: img.naturalHeight,
          rect: box(img),
          filter: s.filter, opacity: s.opacity, objectFit: s.objectFit, display: s.display,
          loading: img.getAttribute('loading'),
          insideShot: Boolean(shot),
          shotProduct: shot ? shot.getAttribute('data-screen-product') : null,
          shotCandidate: shot ? shot.getAttribute('data-screen-candidate') : null,
          shotVariant: shot ? shot.getAttribute('data-screen-variant') : null
        };
      });
      const labels = [...panel.querySelectorAll('.bc-window-label')].map(el => ({ text: (el.textContent || '').trim(), ariaHidden: el.closest('.bc-window-bar') ? el.closest('.bc-window-bar').getAttribute('aria-hidden') : null, rect: box(el) }));
      const headingStyle = getComputedStyle(heading);
      return {
        key: panel.getAttribute('data-bc-panel'),
        rect: box(panel),
        copy: { rect: box(copy), textAlign: getComputedStyle(copy).textAlign, direction: getComputedStyle(copy).direction },
        visual: box(visual),
        heading: { text: (heading.textContent || '').trim(), rect: box(heading), fontSize: headingStyle.fontSize, lineHeight: headingStyle.lineHeight, color: headingStyle.color, scrollWidth: heading.scrollWidth, clientWidth: heading.clientWidth },
        desc: { text: (desc.textContent || '').trim(), rect: box(desc), color: getComputedStyle(desc).color, scrollWidth: desc.scrollWidth, clientWidth: desc.clientWidth, claim: desc.getAttribute('data-claim-fragment') },
        actions: { rect: box(actions), direction: getComputedStyle(actions).direction },
        ctas, images, labels,
        number: (q(panel, '.bc-number') || {}).textContent || null
      };
    });
    const intro = q(section, '.bc-intro');
    const eyebrow = q(section, '.bc-eyebrow');
    const title = q(section, 'h2');
    const introText = q(section, '.bc-intro-text');
    const disclosure = q(section, '.screenshot-disclosure');
    return {
      locale, width, viewport: { width: vw, height: document.documentElement.clientHeight },
      dir: document.documentElement.getAttribute('dir'), lang: document.documentElement.getAttribute('lang'),
      docScrollWidth: doc.scrollWidth, docClientWidth: vw, bodyScrollWidth: document.body.scrollWidth,
      section: { rect: box(section), clientWidth: section.clientWidth, scrollWidth: section.scrollWidth, backgroundColor: cs.backgroundColor, color: cs.color, ariaLabelledby: section.getAttribute('aria-labelledby'), labelText: (document.getElementById(section.getAttribute('aria-labelledby')) || {}).textContent || null },
      intro: intro ? box(intro) : null,
      eyebrow: eyebrow ? { text: (eyebrow.textContent || '').trim(), rect: box(eyebrow), fontSize: getComputedStyle(eyebrow).fontSize } : null,
      title: title ? { text: (title.textContent || '').trim(), rect: box(title), fontSize: getComputedStyle(title).fontSize, lineHeight: getComputedStyle(title).lineHeight, color: getComputedStyle(title).color } : null,
      introText: introText ? { text: (introText.textContent || '').trim(), rect: box(introText), fontSize: getComputedStyle(introText).fontSize, color: getComputedStyle(introText).color, scrollWidth: introText.scrollWidth, clientWidth: introText.clientWidth } : null,
      disclosure: disclosure ? { text: (disclosure.textContent || '').trim(), rect: box(disclosure), fontSize: getComputedStyle(disclosure.querySelector('.screen-disclosure') || disclosure).fontSize } : null,
      panels, panelCount: panels.length, imageCount: panels.reduce((n, p) => n + p.images.length, 0), shotCount: section.querySelectorAll('.bc-shot').length,
      ctaOrder: [...section.querySelectorAll('.bc-cta')].map(a => ({ text: (a.textContent || '').trim(), href: a.getAttribute('href'), left: box(a).left, right: box(a).right })),
      textOverflow,
      focusables: [...section.querySelectorAll('a,button,input,select,textarea,[tabindex]')].filter(el => el.tabIndex >= 0).map(el => ({ tag: el.tagName, text: (el.textContent || '').trim(), tabIndex: el.tabIndex }))
    };
  }, { locale, width });
}

async function runAxe(page, selector) {
  await page.evaluate(AXE_SOURCE);
  return page.evaluate(async ({ selector, tags }) => {
    const target = selector ? document.querySelector(selector) : document;
    const result = await axe.run(target, { runOnly: { type: 'tag', values: tags } });
    const simplify = list => list.map(item => ({
      id: item.id, impact: item.impact, help: item.help,
      nodes: item.nodes.map(node => ({ target: node.target, html: node.html, failureSummary: node.failureSummary }))
    }));
    return { violations: simplify(result.violations), incomplete: simplify(result.incomplete), passes: result.passes.length };
  }, { selector, tags: TAGS });
}

function expectedAlt(locale, product) {
  return MANIFEST.products[product][locale].alt;
}
function expectedImage(locale, product) {
  const image = MANIFEST.products[product][locale].desktop;
  return { path: image.publicPath, width: image.dimensions.width, height: image.dimensions.height, bytes: image.bytes, candidate: image.candidate };
}
async function capture(page, name, locator) {
  const target = locator || page.locator('#capabilities');
  const file = path.join(EVIDENCE, name);
  await target.screenshot({ path: file });
  return file;
}

const browser = await chromium.launch();
try {
  for (const locale of LOCALES) {
    for (const width of WIDTHS) {
      const page = await browser.newPage({ viewport: { width, height: 900 }, deviceScaleFactor: 1 });
      const errors = []; const warnings = []; const failed = [];
      page.on('console', msg => { if (msg.type() === 'error') errors.push(msg.text()); if (msg.type() === 'warning') warnings.push(msg.text()); });
      page.on('pageerror', err => errors.push(String(err)));
      page.on('requestfailed', req => failed.push({ url: req.url(), failure: req.failure() ? req.failure().errorText : null }));
      page.on('response', res => { if (res.status() >= 400) failed.push({ url: res.url(), status: res.status() }); });
      await load(page, locale);
      await warmImages(page);
      const section = page.locator('#capabilities');
      await section.scrollIntoViewIfNeeded();
      await page.waitForTimeout(220);
      const metrics = await collect(page, locale, width);
      const sectionFile = await capture(page, 'bc-' + locale + '-' + width + '-section.png');
      const sectionHeight = await section.evaluate(el => Math.ceil(el.getBoundingClientRect().height));
      await page.setViewportSize({ width, height: Math.min(16000, sectionHeight + 160) });
      await page.evaluate(() => window.scrollTo(0, document.querySelector('#capabilities').offsetTop));
      await page.waitForTimeout(160);
      const axe = await runAxe(page, '#capabilities');
      report.axe[locale + '-' + width] = axe;
      metrics.evidence = path.basename(sectionFile);
      metrics.console = { errors, warnings };
      metrics.failedRequests = failed;
      report.responsive[locale + '-' + width] = metrics;

      check(locale + '-' + width + ' console errors', errors.length === 0, errors);
      check(locale + '-' + width + ' failed requests', failed.length === 0, failed);
      check(locale + '-' + width + ' no document horizontal overflow', metrics.docScrollWidth <= metrics.docClientWidth + 1, { scrollWidth: metrics.docScrollWidth, clientWidth: metrics.docClientWidth });
      check(locale + '-' + width + ' section label resolves', Boolean(metrics.section.labelText), metrics.section.labelText);
      check(locale + '-' + width + ' three panels', metrics.panelCount === 3, metrics.panelCount);
      check(locale + '-' + width + ' four application captures', metrics.imageCount === 4 && metrics.shotCount === 4, { images: metrics.imageCount, shots: metrics.shotCount });
      check(locale + '-' + width + ' no clipped text', metrics.textOverflow.length === 0, metrics.textOverflow);
      check(locale + '-' + width + ' no section horizontal overflow', metrics.section.scrollWidth <= metrics.section.clientWidth + 1, { scrollWidth: metrics.section.scrollWidth, clientWidth: metrics.section.clientWidth });
      check(locale + '-' + width + ' axe violations zero', axe.violations.length === 0, axe.violations);
      check(locale + '-' + width + ' axe incomplete zero', axe.incomplete.length === 0, axe.incomplete);
      check(locale + '-' + width + ' section background solid', /^rgb\(/.test(metrics.section.backgroundColor), metrics.section.backgroundColor);

      const products = { pos: ['pos'], storefront: ['storefront'], channels: ['affiliate', 'commercehub'] };
      for (const panel of metrics.panels) {
        check(locale + '-' + width + ' ' + panel.key + ' heading visible', panel.heading.text.length > 0 && panel.heading.rect.width > 0 && panel.heading.rect.height > 0, panel.heading.text);
        check(locale + '-' + width + ' ' + panel.key + ' heading not clipped', panel.heading.scrollWidth <= panel.heading.clientWidth + 1, { scrollWidth: panel.heading.scrollWidth, clientWidth: panel.heading.clientWidth });
        check(locale + '-' + width + ' ' + panel.key + ' description visible', panel.desc.text.length > 0 && panel.desc.rect.width > 0, panel.desc.text);
        check(locale + '-' + width + ' ' + panel.key + ' description claim', Boolean(panel.desc.claim), panel.desc.claim);
        const expected = products[panel.key];
        check(locale + '-' + width + ' ' + panel.key + ' capture count', panel.images.length === expected.length, panel.images.length);
        for (let i = 0; i < panel.images.length; i++) {
          const img = panel.images[i];
          const product = expected[i];
          const manifestImage = expectedImage(locale, product);
          const manifestAlt = expectedAlt(locale, product);
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' image loaded', img.complete && img.naturalWidth > 0 && img.naturalHeight > 0, { complete: img.complete, naturalWidth: img.naturalWidth, naturalHeight: img.naturalHeight });
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' manifest dimensions', img.naturalWidth === manifestImage.width && img.naturalHeight === manifestImage.height, { actual: [img.naturalWidth, img.naturalHeight], manifest: [manifestImage.width, manifestImage.height] });
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' manifest source', img.src === manifestImage.path, { actual: img.src, manifest: manifestImage.path });
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' localized alt', img.alt === manifestAlt, { actual: img.alt, expected: manifestAlt });
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' independent img element', img.insideShot && img.display !== 'none', { insideShot: img.insideShot, display: img.display });
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' unmodified rendering', img.filter === 'none' && img.opacity === '1' && img.objectFit === 'fill', { filter: img.filter, opacity: img.opacity, objectFit: img.objectFit });
          const bleed = panel.rect.width * 0.01;
          check(locale + '-' + width + ' ' + panel.key + ' ' + product + ' inside panel', img.rect.width > 0 && img.rect.height > 0 && img.rect.x >= panel.rect.x - bleed && img.rect.right <= panel.rect.right + bleed, { img: img.rect, panel: panel.rect, bleed: Math.round(bleed * 100) / 100 });
        }
        for (const cta of panel.ctas) {
          check(locale + '-' + width + ' ' + panel.key + ' CTA visible', cta.visible && cta.rect.width > 0 && cta.rect.height > 0, cta.text);
          check(locale + '-' + width + ' ' + panel.key + ' CTA target size', cta.minSide >= 44, { minSide: cta.minSide, rect: cta.rect });
          check(locale + '-' + width + ' ' + panel.key + ' CTA text not clipped', !cta.textClipped, { scrollWidth: cta.rect.width, text: cta.text });
          check(locale + '-' + width + ' ' + panel.key + ' CTA accessible name is visible text', !cta.ariaLabel && cta.text.length > 0, { ariaLabel: cta.ariaLabel, text: cta.text });
          check(locale + '-' + width + ' ' + panel.key + ' CTA inside panel', cta.rect.x >= panel.rect.x - 1 && cta.rect.right <= panel.rect.right + 1, { cta: cta.rect, panel: panel.rect });
        }
      }
      if (metrics.panels.length === 3) {
        const layouts = metrics.panels.map(p => ({ key: p.key, copyLeft: p.copy.rect.left, visualLeft: p.visual.left, copyRight: p.copy.rect.right, visualRight: p.visual.right }));
        if (width > 900) {
          if (locale === 'ar') {
            check(locale + '-' + width + ' P1 copy right of visual', layouts[0].copyLeft > layouts[0].visualLeft, layouts[0]);
            check(locale + '-' + width + ' P2 copy left of visual', layouts[1].copyLeft < layouts[1].visualLeft, layouts[1]);
          } else {
            check(locale + '-' + width + ' P1 copy left of visual', layouts[0].copyLeft < layouts[0].visualLeft, layouts[0]);
            check(locale + '-' + width + ' P2 copy right of visual', layouts[1].copyLeft > layouts[1].visualLeft, layouts[1]);
          }
        }
        if (width > 1100) {
          if (locale === 'ar') check(locale + '-' + width + ' P3 copy right of visual', layouts[2].copyLeft > layouts[2].visualLeft, layouts[2]);
          else check(locale + '-' + width + ' P3 copy left of visual', layouts[2].copyLeft < layouts[2].visualLeft, layouts[2]);
        }
        const p3 = metrics.panels[2];
        if (p3.ctas.length === 2) {
          const first = p3.ctas[0]; const second = p3.ctas[1];
          if (width > 640) check(locale + '-' + width + ' Affiliate before CommerceHub', first.rect.left < second.rect.left, p3.ctas.map(c => ({ text: c.text, left: c.rect.left })));
          else check(locale + '-' + width + ' Affiliate above CommerceHub', first.top < second.top, p3.ctas.map(c => ({ text: c.text, top: c.top })));
        }
      }
      await page.close();
    }
  }

  // Whole-page axe at 1440 and a direct comparison to the Phase 1C build.
  for (const locale of LOCALES) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await load(page, locale);
    report.fullPageAxe[locale] = await runAxe(page, null);
    await page.close();
    const basePage = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await basePage.goto(BASELINE + '/' + locale + '/', { waitUntil: 'networkidle' });
    await basePage.evaluate(() => document.fonts.ready);
    report.fullPageAxeBaseline[locale] = await runAxe(basePage, null);
    report.axeBaseline[locale] = await runAxe(basePage, '#capabilities');
    await basePage.close();
  }

  // Links: resolve, HTTP status and destination anchor.
  for (const locale of LOCALES) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await load(page, locale);
    const links = await page.$$eval('.bc-cta', as => as.map(a => ({ text: (a.textContent || '').trim(), raw: a.getAttribute('href'), href: a.href })));
    report.links[locale] = [];
    for (const link of links) {
      const url = new URL(link.href);
      const response = await page.request.get(url.origin + url.pathname);
      const status = response.status();
      await page.goto(link.href, { waitUntil: 'domcontentloaded' });
      const destination = await page.evaluate(() => ({ hash: location.hash, main: Boolean(document.querySelector('#main')), title: document.title, h1: document.querySelector('h1') ? document.querySelector('h1').textContent.trim() : null }));
      const record = { ...link, path: url.pathname, hash: url.hash, status, destination };
      report.links[locale].push(record);
      check(locale + ' link ' + link.text + ' HTTP 200', status === 200, status);
      check(locale + ' link ' + link.text + ' destination #main', destination.main && destination.hash === '#main', destination);
      await load(page, locale);
    }
    await page.close();
  }

  // Keyboard focus order and visible focus.
  for (const locale of LOCALES) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await load(page, locale);
    const first = page.locator('.bc-cta').first();
    await first.focus();
    const focus = await page.evaluate(() => {
      const el = document.activeElement; const s = getComputedStyle(el);
      return { tag: el.tagName, text: (el.textContent || '').trim(), outlineStyle: s.outlineStyle, outlineWidth: s.outlineWidth, outlineColor: s.outlineColor, outlineOffset: s.outlineOffset };
    });
    await page.locator('[data-bc-panel="pos"]').screenshot({ path: path.join(EVIDENCE, 'bc-' + locale + '-keyboard-focus.png') });
    const order = [focus];
    for (let i = 0; i < 3; i++) {
      await page.keyboard.press('Tab');
      order.push(await page.evaluate(() => { const el = document.activeElement; return { tag: el.tagName, text: (el.textContent || '').trim(), href: el.getAttribute ? el.getAttribute('href') : null }; }));
    }
    const visibleOutline = parseFloat(focus.outlineWidth) >= 2 && focus.outlineStyle !== 'none';
    report.keyboard[locale] = { focus, order, visibleOutline };
    check(locale + ' keyboard first focus is first CTA', focus.tag === 'A' && focus.text.length > 0, focus);
    check(locale + ' keyboard visible focus', visibleOutline, focus);
    check(locale + ' keyboard reaches four CTAs', order.filter(item => item.tag === 'A').length === 4, order);
    const expectedTexts = locale === 'en' ? ['Explore POS Admin', 'Explore Storefront', 'Affiliate System', 'CommerceHub'] : ['استكشف إدارة نقاط البيع', 'استكشف المتجر الإلكتروني', 'Affiliate System', 'CommerceHub'];
    check(locale + ' keyboard order matches DOM', order.every((item, i) => item.tag === 'A' && item.text === expectedTexts[i]), order);
    await page.close();
  }

  // Reduced motion.
  for (const locale of LOCALES) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' });
    await load(page, locale);
    const result = await page.evaluate(() => {
      const panel = document.querySelector('[data-bc-panel="pos"]');
      const cta = document.querySelector('.bc-cta');
      return {
        media: matchMedia('(prefers-reduced-motion: reduce)').matches,
        panelTransition: getComputedStyle(panel).transitionDuration,
        ctaTransition: getComputedStyle(cta).transitionDuration,
        panelTransform: getComputedStyle(panel).transform,
        ctaTransform: getComputedStyle(cta).transform
      };
    });
    await page.locator('.bc-cta').first().hover();
    await page.waitForTimeout(120);
    const hovered = await page.evaluate(() => ({ cta: getComputedStyle(document.querySelector('.bc-cta')).transform, panel: getComputedStyle(document.querySelector('[data-bc-panel="pos"]')).transform }));
    await page.locator('#capabilities').screenshot({ path: path.join(EVIDENCE, 'bc-' + locale + '-reduced-motion.png') });
    report.reducedMotion[locale] = { ...result, hovered };
    check(locale + ' reduced motion active', result.media === true, result.media);
    check(locale + ' reduced motion removes transitions', result.panelTransition === '0s' && result.ctaTransition === '0s', result);
    check(locale + ' reduced motion removes hover transforms', hovered.cta === 'none' && hovered.panel === 'none', hovered);
    await page.close();
  }

  // 200% text-only scaling at desktop and mobile.
  for (const locale of LOCALES) {
    for (const width of [1440, 375]) {
      const page = await browser.newPage({ viewport: { width, height: 900 } });
      await load(page, locale);
      await warmImages(page);
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      await page.waitForTimeout(350);
      const metrics = await collect(page, locale, width);
      metrics.evidence = path.basename(await capture(page, 'bc-' + locale + '-' + width + '-text-200.png'));
      report.textScale[locale + '-' + width] = metrics;
      check(locale + '-' + width + ' 200% no document overflow', metrics.docScrollWidth <= metrics.docClientWidth + 1, { scrollWidth: metrics.docScrollWidth, clientWidth: metrics.docClientWidth });
      check(locale + '-' + width + ' 200% no clipped text', metrics.textOverflow.length === 0, metrics.textOverflow);
      check(locale + '-' + width + ' 200% all CTAs visible', metrics.panels.every(p => p.ctas.every(c => c.visible && c.rect.width > 0 && c.rect.height > 0)), metrics.panels.map(p => p.ctas.map(c => ({ text: c.text, visible: c.visible, rect: c.rect }))));
      check(locale + '-' + width + ' 200% CTAs within panels', metrics.panels.every(p => p.ctas.every(c => c.rect.x >= p.rect.x - 1 && c.rect.right <= p.rect.right + 1)), metrics.panels.map(p => ({ key: p.key, panel: p.rect, ctas: p.ctas.map(c => c.rect) })));
      check(locale + '-' + width + ' 200% images loaded', metrics.panels.every(p => p.images.every(img => img.complete && img.naturalWidth > 0)), metrics.panels.map(p => p.images.map(img => ({ src: img.src, complete: img.complete, naturalWidth: img.naturalWidth }))));
      check(locale + '-' + width + ' 200% images inside panels', metrics.panels.every(p => p.images.every(img => { const bleed = p.rect.width * 0.01; return img.rect.x >= p.rect.x - bleed && img.rect.right <= p.rect.right + bleed; })), metrics.panels.map(p => ({ key: p.key, panel: p.rect, images: p.images.map(img => img.rect) })));
      await page.close();
    }
  }

  // Image HTTP delivery check.
  {
    const page = await browser.newPage();
    for (const locale of LOCALES) {
      for (const product of ['pos', 'storefront', 'affiliate', 'commercehub']) {
        const image = expectedImage(locale, product);
        const response = await page.request.get(BASE + image.path);
        const body = await response.body();
        const record = { locale, product, path: image.path, status: response.status(), contentType: response.headers()['content-type'], bytes: body.length, manifestBytes: image.bytes };
        report.images[locale + '-' + product] = record;
        check(locale + ' ' + product + ' image HTTP 200', response.status() === 200, record);
        check(locale + ' ' + product + ' image webp', /image\/webp/.test(record.contentType || ''), record.contentType);
        check(locale + ' ' + product + ' image bytes match manifest', body.length === image.bytes, { actual: body.length, manifest: image.bytes });
      }
    }
    await page.close();
  }

  // Full-page axe comparison summary.
  for (const locale of LOCALES) {
    const before = report.fullPageAxeBaseline[locale];
    const after = report.fullPageAxe[locale];
    const beforeIds = new Set(before.violations.map(v => v.id));
    const afterIds = new Set(after.violations.map(v => v.id));
    check(locale + ' full page no new violation rule', [...afterIds].every(id => beforeIds.has(id)), { before: [...beforeIds], after: [...afterIds] });
    check(locale + ' section axe zero (baseline comparison)', report.axe[locale + '-1440'].violations.length === 0 && report.axe[locale + '-1440'].incomplete.length === 0, report.axe[locale + '-1440']);
  }

  report.summary = {
    checks: report.checks.length,
    failures: report.failures.length,
    warnings: report.warnings.length,
    axeSectionRuns: Object.keys(report.axe).length,
    axeSectionViolations: Object.values(report.axe).reduce((n, r) => n + r.violations.length, 0),
    axeSectionIncomplete: Object.values(report.axe).reduce((n, r) => n + r.incomplete.length, 0),
    fullPageViolations: Object.fromEntries(Object.entries(report.fullPageAxe).map(([k, v]) => [k, v.violations.map(x => x.id)])),
    baselineFullPageViolations: Object.fromEntries(Object.entries(report.fullPageAxeBaseline).map(([k, v]) => [k, v.violations.map(x => x.id)]))
  };
  fs.writeFileSync(path.join(EVIDENCE, 'verification.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report.summary, null, 2));
  if (report.failures.length) {
    console.log('FAILURES');
    for (const failure of report.failures) console.log('-', failure.name, JSON.stringify(failure.detail));
    process.exitCode = 1;
  }
} finally {
  await browser.close();
}