// Phase 1C qualification: Ecosystem Relationships on the homepage, EN + AR,
// five widths, plus 200% text scaling. Review build only (MARKETING_DIST).
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { pagePath } from '../src/routes.mjs';
import { createPreview } from '../scripts/preview.mjs';

const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'temp/phase1c-test'));
await fs.mkdir(path.join(evidence, 'screenshots'), { recursive: true });
const require = createRequire(import.meta.url);
const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
const labels = JSON.parse(await fs.readFile(path.join(app, 'src/content/labels.json'), 'utf8'));
const claims = JSON.parse(await fs.readFile(path.join(app, 'src/content/claims.json'), 'utf8'));
const widths = [1440, 1024, 768, 375, 320];
const scaleWidths = [1440, 1024, 768, 375];
const locales = ['en', 'ar'];
const BRAND = { pos: 'POS Admin', storefront: 'Storefront', affiliate: 'Affiliate System', commercehub: 'CommerceHub' };
const EXPECT = [
  { n: '01', chips: ['pos', 'storefront', 'commercehub'], art: 'svg' },
  { n: '02', chips: ['pos', 'storefront'], art: 'svg' },
  { n: '03', chips: ['affiliate', 'storefront'], art: 'svg' },
  { n: '04', chips: ['pos', 'commercehub'], art: 'svg' },
  { n: '05', chips: ['storefront', 'commercehub'], art: 'svg' },
  { n: '06', chips: ['pos', 'storefront', 'affiliate', 'commercehub'], art: 'html' }
];
const claims_ = ['C06', 'C31', 'C35'];
const results = { startedUtc: new Date().toISOString(), origin: 'loopback preview', states: [], axe: [], assertions: [], errors: [], consoleErrors: [], externalRequests: [], brokenResponses: [] };
const check = (pass, label, detail) => results.assertions.push({ pass: Boolean(pass), label, ...(detail === undefined ? {} : { detail }) });
const squash = value => String(value).replace(/\s+/g, ' ').trim();

function audit() {
  const parseColor = (value) => {
    const m = /rgba?\(([^)]+)\)/.exec(value || '');
    if (!m) return null;
    const parts = m[1].split(/[ ,/]+/).filter(Boolean).map(Number);
    return { r: parts[0], g: parts[1], b: parts[2], a: parts.length > 3 ? parts[3] : 1 };
  };
  const effectiveBg = (el) => {
    const stack = [];
    for (let node = el; node; node = node.parentElement) {
      const c = parseColor(getComputedStyle(node).backgroundColor);
      if (!c || c.a === 0) continue;
      stack.push(c);
      if (c.a >= 1) break;
    }
    let base = stack.length && stack[stack.length - 1].a >= 1 ? stack.pop() : { r: 255, g: 255, b: 255, a: 1 };
    for (let i = stack.length - 1; i >= 0; i -= 1) {
      const top = stack[i];
      base = { r: top.r * top.a + base.r * (1 - top.a), g: top.g * top.a + base.g * (1 - top.a), b: top.b * top.a + base.b * (1 - top.a), a: 1 };
    }
    return base;
  };
  const channel = (v) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); };
  const lum = (c) => 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b);
  const ratio = (a, b) => { const l1 = lum(a); const l2 = lum(b); return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05); };
  const measure = (el, label) => {
    if (!el) return null;
    const cs = getComputedStyle(el);
    const fg = parseColor(cs.color);
    const bg = effectiveBg(el);
    return { label, fg: cs.color, bg: 'rgb(' + Math.round(bg.r) + ' ' + Math.round(bg.g) + ' ' + Math.round(bg.b) + ')', ratio: Number(ratio(fg, bg).toFixed(2)), fontSize: Number(parseFloat(cs.fontSize).toFixed(2)), weight: cs.fontWeight };
  };
  const rect = (el) => { const b = el.getBoundingClientRect(); return { x: Number(b.x.toFixed(1)), y: Number(b.y.toFixed(1)), w: Number(b.width.toFixed(1)), h: Number(b.height.toFixed(1)) }; };
  const section = document.querySelector('.er-section');
  if (!section) return { missing: true };
  const css = getComputedStyle(section);
  const cards = [...section.querySelectorAll('.er-card')];
  const rows = [];
  for (const card of cards) {
    const top = Math.round(card.getBoundingClientRect().top);
    const row = rows.find((r) => Math.abs(r.top - top) <= 2);
    if (row) row.count += 1; else rows.push({ top, count: 1 });
  }
  const protruders = (card) => {
    const box = card.getBoundingClientRect();
    return [...card.querySelectorAll('*')].filter((el) => {
      const r = el.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) return false;
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.display === 'none' || cs.opacity === '0') return false;
      return r.right > box.right + 1.5 || r.left < box.left - 1.5 || r.bottom > box.bottom + 1.5 || r.top < box.top - 1.5;
    }).map((el) => ({ tag: el.tagName, cls: typeof el.className === 'string' ? el.className : el.getAttribute('class'), text: (el.textContent || '').slice(0, 20) })).slice(0, 8);
  };
  const cards_ = cards.map((card, index) => {
    const title = card.querySelector('.er-title');
    const desc = card.querySelector('.er-desc');
    const chips = [...card.querySelectorAll('.er-chip')];
    const svg = card.querySelector('.er-art-svg');
    const roles = [...card.querySelectorAll('.er-role')];
    const roleLabel = card.querySelector('.er-role-label');
    const boundsBox = card.getBoundingClientRect();
    const titleBox = title.getBoundingClientRect();
    const lineHeight = parseFloat(getComputedStyle(title).lineHeight);
    return {
      index,
      n: card.querySelector('.er-number').textContent.trim(),
      title: title.textContent.trim(),
      desc: desc.textContent.trim(),
      titleLines: Math.round(titleBox.height / lineHeight),
      titleRect: rect(title),
      rect: rect(card),
      accent: [...card.classList].filter((c) => c.startsWith('er-accent-')).join(' '),
      chips: chips.map((chip) => ({ text: chip.textContent.trim(), dir: chip.querySelector('bdi') ? chip.querySelector('bdi').getAttribute('dir') : null, tag: chip.querySelector('bdi') ? chip.querySelector('bdi').tagName : null, rect: rect(chip) })),
      chipRows: new Set(chips.map((c) => Math.round(c.getBoundingClientRect().top))).size,
      svg: svg ? { viewBox: svg.getAttribute('viewBox'), ariaHidden: svg.getAttribute('aria-hidden'), focusable: svg.getAttribute('focusable'), transform: getComputedStyle(svg).transform, rect: rect(svg) } : null,
      roles: roles.map((role) => ({ label: role.textContent.trim(), rect: rect(role), dir: getComputedStyle(role).direction })),
      rolesColumns: card.querySelector('.er-roles') ? getComputedStyle(card.querySelector('.er-roles')).gridTemplateColumns : null,
      protruders: protruders(card),
      art: card.querySelector('.er-art') ? rect(card.querySelector('.er-art')) : null,
      bodyBelowTitle: desc.getBoundingClientRect().top - titleBox.bottom,
      gapToArt: card.querySelector('.er-art') ? card.querySelector('.er-art').getBoundingClientRect().top - desc.getBoundingClientRect().bottom : null,
      contrast: [
        measure(title, 'title'),
        measure(desc, 'desc'),
        measure(card.querySelector('.er-number'), 'number'),
        measure(chips[0], 'chip'),
        measure(roleLabel, 'role-label'),
        measure(card.querySelector('.er-fee-sep'), 'fee-sep'),
        measure(card.querySelector('.er-fee-tag'), 'fee-tag')
      ].filter(Boolean),
      bg: css
    };
  });
  const boundsList = section.querySelector('.er-bounds-list');
  const bounds = [...section.querySelectorAll('.er-bounds-list [data-claim]')].map((el) => ({ id: el.getAttribute('data-claim'), text: (el.textContent || '').trim(), tag: el.tagName }));
  const headings = [...section.querySelectorAll('h1,h2,h3,h4,h5,h6')].map((h) => ({ level: h.tagName, id: h.id || null, text: h.textContent.trim(), rect: rect(h) }));
  const focusable = section.querySelectorAll('a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])');
  return {
    lang: document.documentElement.lang,
    dir: document.documentElement.dir,
    rootFontSize: parseFloat(getComputedStyle(document.documentElement).fontSize),
    viewport: { w: document.documentElement.clientWidth, h: window.innerHeight },
    scrollWidth: document.documentElement.scrollWidth,
    section: { rect: rect(section), id: section.id, labelledby: section.getAttribute('aria-labelledby'), bg: css.backgroundColor, isolation: css.isolation, overflowX: css.overflowX },
    intro: {
      eyebrow: section.querySelector('.er-eyebrow') ? section.querySelector('.er-eyebrow').textContent.trim() : null,
      heading: section.querySelector('#er-heading') ? section.querySelector('#er-heading').textContent.trim() : null,
      sub: section.querySelector('.er-sub') ? section.querySelector('.er-sub').textContent.trim() : null
    },
    grid: { columns: getComputedStyle(section.querySelector('.er-grid')).gridTemplateColumns, rows, gap: getComputedStyle(section.querySelector('.er-grid')).gap },
    cards: cards_,
    headings,
    bounds: { list: bounds, columns: boundsList ? getComputedStyle(boundsList).gridTemplateColumns : null, title: section.querySelector('.er-bounds-title') ? section.querySelector('.er-bounds-title').textContent.trim() : null, contrast: [measure(section.querySelector('.er-bounds-title'), 'bounds-title'), measure(section.querySelector('.er-bounds-list p'), 'bounds-body')].filter(Boolean) },
    introContrast: [measure(section.querySelector('.er-eyebrow'), 'eyebrow'), measure(section.querySelector('#er-heading'), 'h2'), measure(section.querySelector('.er-sub'), 'sub')].filter(Boolean),
    focusable: focusable.length,
    h1Count: document.querySelectorAll('h1').length,
    h1Text: document.querySelector('h1') ? document.querySelector('h1').textContent.trim() : null
  };
}

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

  const load = async (locale, width, scale) => {
    await page.setViewportSize({ width, height: width < 600 ? 900 : 1000 });
    await page.goto(origin + pagePath(locale, 'home'), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    if (scale === 200) await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await page.locator('.er-section').scrollIntoViewIfNeeded();
    await page.waitForTimeout(120);
  };

  const matrix = [...widths.map(w => ({ width: w, scale: 100 })), ...scaleWidths.map(w => ({ width: w, scale: 200 }))];
  for (const locale of locales) {
    for (const { width: realWidth, scale } of matrix) {
      const prefix = `${locale}-${realWidth}-${scale}`;
      await load(locale, realWidth, scale);
      const state = await page.evaluate(audit);
      await page.evaluate(axeSource);
      const auditResult = await page.evaluate(async () => {
        const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice'] } });
        const detail = node => ({
          target: node.target.join(' '),
          messageKey: node.any[0] && node.any[0].data ? node.any[0].data.messageKey : null
        });
        return {
          violations: result.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.map(detail) })),
          incomplete: result.incomplete.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.map(detail) })),
          passes: result.passes.length
        };
      });
      results.axe.push({ prefix, ...auditResult });
      results.states.push({ prefix, locale, width: realWidth, scale, ...state });
      await page.locator('.er-section').screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-section.png`) });
      if (scale === 100 && realWidth === 1440) {
        await page.screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-full.png`), fullPage: true });
        await page.locator('.er-card').nth(5).screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-card06.png`) });
        await page.locator('.er-card').nth(0).screenshot({ path: path.join(evidence, 'screenshots', `${prefix}-card01.png`) });
      }
    }
  }
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}

// ------------------------------------------------------------ assertions
const base = (locale, width) => results.states.find(s => s.locale === locale && s.width === width && s.scale === 100);
for (const state of results.states) {
  const prefix = state.prefix;
  const text = labels[state.locale];
  const size = state.scale;
  check(state.section.id === 'relationships', `Section keeps id relationships ${prefix}`);
  check(state.section.labelledby === 'er-heading', `Section is labelled by its heading ${prefix}`, state.section.labelledby);
  check(state.cards.length === 6, `Exactly six relationship cards ${prefix}`, state.cards.length);
  check(state.cards.map(c => c.n).join(',') === '01,02,03,04,05,06', `Card order 01-06 in DOM ${prefix}`, state.cards.map(c => c.n));
  check(state.cards.every((c, i) => c.title === text['er' + (i + 1) + 'title']), `Card titles match approved copy ${prefix}`, state.cards.map(c => c.title));
  check(state.cards.every((c, i) => c.desc === text['er' + (i + 1) + 'desc']), `Card descriptions match approved copy ${prefix}`, state.cards.map(c => c.desc));
  check(state.intro.heading === text.erheading, `Intro heading matches copy ${prefix}`, state.intro.heading);
  check(state.intro.sub === text.ersub, `Intro subline matches copy ${prefix}`, state.intro.sub);
  check(state.intro.eyebrow === text.ereyebrow, `Eyebrow matches copy ${prefix}`, state.intro.eyebrow);
  state.cards.forEach((card, i) => {
    const expected = EXPECT[i];
    check(card.chips.length === expected.chips.length, `Card ${card.n} chip count ${prefix}`, card.chips.length);
    check(card.chips.every((chip, j) => chip.text === BRAND[expected.chips[j]]), `Card ${card.n} chip products ${prefix}`, card.chips.map(c => c.text));
    check(card.chips.every(chip => chip.tag === 'BDI' && chip.dir === 'ltr'), `Card ${card.n} brand tokens isolated ${prefix}`, card.chips.map(c => [c.tag, c.dir]));
    check(card.protruders.length === 0, `Card ${card.n} content stays inside its frame ${prefix}`, card.protruders);
    check(card.titleLines <= 4, `Card ${card.n} title stays within four lines ${prefix}`, card.titleLines);
    if (expected.art === 'svg') {
      check(card.svg && card.svg.ariaHidden === 'true' && card.svg.focusable === 'false', `Card ${card.n} illustration is decorative ${prefix}`, card.svg);
      check(card.svg && card.svg.viewBox === '0 0 320 132', `Card ${card.n} illustration keeps its viewBox ${prefix}`, card.svg && card.svg.viewBox);
      check(card.svg && card.svg.transform === 'none', `Card ${card.n} illustration is not mirrored ${prefix}`, card.svg && card.svg.transform);
    } else {
      check(card.roles.length === 4, `Card ${card.n} lists four audience roles ${prefix}`, card.roles.length);
      check(card.roles.every(role => role.label.length > 2), `Card ${card.n} role labels are real text ${prefix}`);
    }
    check(card.contrast.every(item => item.ratio >= 4.5), `Card ${card.n} text contrast >= 4.5 ${prefix}`, card.contrast.map(c => [c.label, c.ratio]));
  });
  check(state.introContrast.every(item => item.ratio >= (item.label === 'h2' ? 3 : 4.5)), `Intro contrast meets AA ${prefix}`, state.introContrast.map(c => [c.label, c.ratio]));
  check(state.bounds.contrast.every(item => item.ratio >= 4.5), `Boundary band contrast >= 4.5 ${prefix}`, state.bounds.contrast.map(c => [c.label, c.ratio]));
  check(state.bounds.list.map(b => b.id).join(',') === claims_.join(','), `Boundary claims C06/C31/C35 ${prefix}`, state.bounds.list.map(b => b.id));
  check(state.bounds.list.every(b => squash(b.text) === squash(claims[b.id][state.locale])), `Boundary claim text matches claims.json ${prefix}`, state.bounds.list.map(b => b.text.slice(0, 40)));
  check(state.focusable === 0, `Section exposes no interactive controls ${prefix}`, state.focusable);
  check(state.h1Count === 1 && !state.headings.some(h => h.level === 'H1'), `Single page h1 stays outside the section ${prefix}`, state.h1Text);
  check(state.headings.map(h => h.level).join(',') === 'H2,H3,H3,H3,H3,H3,H3,H3', `Section heading structure h2 + seven h3 ${prefix}`, state.headings.map(h => h.level));
  check(state.scrollWidth <= state.viewport.w + 1, `No horizontal page overflow ${prefix}`, [state.scrollWidth, state.viewport.w]);
  const expectedColumns = state.width >= 1200 ? 3 : state.width >= 720 ? 2 : 1;
  const columns = state.grid.columns.split(' ').filter(Boolean).length;
  check(columns === expectedColumns, `Grid uses ${expectedColumns} column(s) at ${state.width}px ${prefix}`, state.grid.columns);
  check(state.grid.rows.every(row => row.count === expectedColumns), `Every grid row is filled ${prefix}`, state.grid.rows);
  check(state.grid.rows.length === 6 / expectedColumns, `Grid row count ${prefix}`, state.grid.rows.length);
  const firstRow = state.cards.filter(c => Math.abs(c.rect.y - state.cards[0].rect.y) <= 2);
  check(firstRow.every(c => Math.abs(c.rect.h - firstRow[0].rect.h) <= 1), `Cards in a row share a height ${prefix}`, firstRow.map(c => c.rect.h));
  if (expectedColumns > 1) {
    const xs = state.cards.slice(0, expectedColumns).map(c => c.rect.x);
    const ordered = state.locale === 'ar' ? xs.every((x, i) => i === 0 || x < xs[i - 1]) : xs.every((x, i) => i === 0 || x > xs[i - 1]);
    check(ordered, `Reading order follows document direction ${prefix}`, xs);
    const secondRow = state.cards.slice(expectedColumns);
    check(secondRow.every(c => c.rect.y > state.cards[0].rect.y + 50), `Second row sits below the first ${prefix}`);
  } else {
    check(state.cards.every((c, i) => i === 0 || c.rect.y > state.cards[i - 1].rect.y + 20), `Cards stack in order ${prefix}`);
  }
  const axe = results.axe.find(a => a.prefix === prefix);
  const inSection = node => /\.er-|#relationships|#er-heading/.test(node.target);
  const sectionIncomplete = axe.incomplete.flatMap(item => item.nodes.filter(inSection));
  const outsideIncomplete = axe.incomplete.flatMap(item => item.nodes.filter(node => !inSection(node)));
  check(axe && axe.violations.length === 0, `axe violations 0 ${prefix}`, axe && axe.violations);
  check(sectionIncomplete.length === 0, `axe incomplete 0 inside #relationships ${prefix}`, sectionIncomplete);
  check(outsideIncomplete.length === 0 || outsideIncomplete.every(node => /\.product-tile-name/.test(node.target)),
    `axe incomplete outside the section limited to the known pre-existing showcase node ${prefix}`, outsideIncomplete);
  if (size === 200) {
    const reference = base(state.locale, state.width);
    check(reference && state.cards[0].contrast && reference.cards[0].contrast, `Text-scale reference available ${prefix}`);
    const scaled = state.cards[0].contrast.find(c => c.label === 'desc').fontSize;
    const plain = reference.cards[0].contrast.find(c => c.label === 'desc').fontSize;
    check(scaled >= plain * 1.9, `Body copy doubles at 200% text ${prefix}`, [plain, scaled]);
    check(state.cards.map(c => c.title).join('|') === reference.cards.map(c => c.title).join('|'), `No card copy is lost at 200% text ${prefix}`);
  }
}

const failed = results.assertions.filter(a => !a.pass);
results.summary = {
  states: results.states.length,
  assertions: results.assertions.length,
  failedAssertions: failed.length,
  failedLabels: failed.slice(0, 20).map(a => a.label),
  axeAudits: results.axe.length,
  axeViolations: results.axe.reduce((total, item) => total + item.violations.length, 0),
  axeIncompleteInSection: results.axe.reduce((total, item) => total + item.incomplete.flatMap(i => i.nodes.filter(n => /\.er-|#relationships|#er-heading/.test(n.target))).length, 0),
  axeIncompleteOutsideSection: results.axe.reduce((total, item) => total + item.incomplete.flatMap(i => i.nodes.filter(n => !/\.er-|#relationships|#er-heading/.test(n.target))).length, 0),
  axeIncompleteOutsideTargets: [...new Set(results.axe.flatMap(item => item.incomplete.flatMap(i => i.nodes.filter(n => !/\.er-|#relationships|#er-heading/.test(n.target)).map(n => n.target))))],
  consoleErrors: results.consoleErrors.length,
  externalRequests: results.externalRequests.length,
  brokenResponses: results.brokenResponses.length,
  errors: results.errors.length
};
results.pass = results.summary.failedAssertions === 0 && results.summary.axeViolations === 0 && results.summary.axeIncompleteInSection === 0 && results.summary.consoleErrors === 0 && results.summary.externalRequests === 0 && results.summary.brokenResponses === 0 && results.summary.errors === 0;
await fs.writeFile(path.join(evidence, 'qualification.json'), JSON.stringify(results, null, 2) + String.fromCharCode(10));
console.log(JSON.stringify(results.summary, null, 2));
if (!results.pass) process.exitCode = 1;