import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { routes, pages, locales, pagePath, siteOrigin, accessDestinations, registrationDestination } from '../src/routes.mjs';
import { descriptionClaims } from '../src/metadata.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = path.resolve(app, '..');
const read = name => fs.readFileSync(path.join(app, name), 'utf8');
const claims = JSON.parse(read('src/content/claims.json'));
const labels = JSON.parse(read('src/content/labels.json'));
const products = JSON.parse(read('src/content/products.json'));
// MARKETING_DIST lets the suite run against an isolated review build without
// touching the committed dist/ output. Defaults to dist/.
const distDir = path.resolve(process.env.MARKETING_DIST || path.join(app, 'dist'));
const readDist = file => fs.readFileSync(path.join(distDir, file), 'utf8');
const receipt = JSON.parse(readDist('build-receipt.json'));
const html = r => readDist(`${r.path.slice(1).replace(/\/$/, '')}/index.html`);
const decode = s => s.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#x27;|&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>');
const plain = s => decode(s.replace(/<[^>]*>/g, '')).replace(/\s+/g, ' ').trim();
const attr = (h, regex) => decode(h.match(regex)?.[1] || '');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');

test('twelve explicit, reciprocal route definitions and nonindexable Arabic root fallback', () => {
  assert.equal(routes.length, 12);
  assert.equal(new Set(routes.map(r => r.path)).size, 12);
  for (const locale of locales) for (const page of pages) {
    const r = routes.find(r => r.path === pagePath(locale, page));
    assert.ok(r); assert.equal(routes.find(x => x.path === r.equivalent).equivalent, r.path);
  }
  const rootHTML = readDist('index.html');
  assert.match(rootHTML, /href="\/ar\/"/);
  assert.match(rootHTML, /noindex, follow/);
  assert.match(rootHTML, /content="0;url=\/ar\/"/);
  assert.doesNotMatch(rootHTML, /Choose your language|اختر لغتك|navigator\.language|geolocation/i);
});
test('all 56 bilingual claims and source evidence/approval statuses remain exact', () => {
  const register = fs.readFileSync(path.join(root, 'docs/marketing/phase-1-discovery-20261121-01/SHOPYXI-MARKETING-CLAIMS-REGISTER.md'), 'utf8');
  let count = 0;
  for (const line of register.split(/\r?\n/)) {
    const m = line.match(/^\| (C\d+[a-z]?) · EN; AR \| \*\*EN:\*\* (.*?)<br>\*\*AR:\*\* (.*?) \| (.*?) \| (.*?) \| (.*?) \|/);
    if (!m) continue;
    assert.deepEqual(claims[m[1]], { en: m[2], ar: m[3], evidence: m[4], status: m[5], approvers: m[6], approval: 'PENDING' });
    count++;
  }
  assert.equal(count, 56);
});
test('provenance records the 56-claim register and its exact audited hash', () => {
  const provenance = JSON.parse(read('src/content/provenance.json'));
  const registerPath = 'docs/marketing/phase-1-discovery-20261121-01/SHOPYXI-MARKETING-CLAIMS-REGISTER.md';
  assert.equal(provenance.claims, Object.keys(claims).length);
  assert.equal(provenance.claims, 56);
  assert.equal(provenance.sources[registerPath], sha(fs.readFileSync(path.join(root, registerPath))));
  assert.ok(Object.values(claims).every(c => c.approval === 'PENDING'));
});
for (const r of routes) test(`${r.path}: static content, SEO, links, exact claims and disclosures`, () => {
  const h = html(r);
  assert.match(h, new RegExp(`<html lang="${r.locale}" dir="${r.dir}">`));
  assert.equal((h.match(/<h1\b/g) || []).length, 1);
  assert.ok(h.includes('<main id="main" tabindex="-1">'));
  assert.equal(attr(h, /<title>(.*?)<\/title>/), `${labels[r.locale][r.page]} — SHOPYXI`);
  assert.equal(attr(h, /<meta name="description" content="([^"]*)"/), claims[descriptionClaims[r.page]][r.locale]);
  assert.equal(attr(h, /<link rel="canonical" href="([^"]*)"/), siteOrigin + r.path);
  for (const locale of locales) assert.ok(h.includes(`hrefLang="${locale}"`) || h.includes(`hrefLang="${locale}"`.toLowerCase()) || h.includes(`href="${siteOrigin}${pagePath(locale, r.page)}"`));
  assert.ok(h.includes(`href="${r.equivalent}#locale"`));
  assert.match(h, /noindex, nofollow, noarchive/);
  assert.match(h, /data-review-only="true"/);
  assert.match(h, /property="og:image" content="https:\/\/shopyxi.com\/assets\/shopyxi-logo.png"/);
  const body = h.slice(h.indexOf('<body'));
  let renderedClaims = 0;
  for (const m of body.matchAll(/<(\w+)([^>]*\bdata-claim="(C\d+)"[^>]*)>([\s\S]*?)<\/\1>/g)) {
    assert.equal(plain(m[4]), claims[m[3]][r.locale], `Exact rendered ${m[3]}`);
    assert.match(m[2], /data-approval="PENDING"/); renderedClaims++;
  }
  // Phase 1F-D replaced the review-only Demo placeholder with the Free
  // Experience CTA (C52-C55, with C55 repeated in the homepage footer). The
  // home floor counts those occurrences; every other route keeps the 15 floor.
  assert.ok(renderedClaims >= (r.page === 'home' ? 16 : 15), `Rendered claim floor (${renderedClaims})`);
  assert.doesNotMatch(body, /data-claim="C42"/);
  const ids = [...h.matchAll(/\sid="([^"]+)"/g)].map(m => m[1]);
  assert.equal(ids.length, new Set(ids).size, 'Unique element IDs');
  for (const m of h.matchAll(/\bhref="([^"]+)"/g)) {
    const link = decode(m[1]);
    if (link.startsWith('#')) assert.ok(ids.includes(link.slice(1)), `Valid section ${link}`);
    if (link.startsWith('/') && !link.startsWith('/assets/')) {
      const [pathname] = link.split('#');
      assert.ok(pathname === '/' || routes.some(x => x.path === pathname), `Valid route ${link}`);
    }
  }
  const externalBodyLinks = [...body.matchAll(/href="(https:[^"]*)"/g)].map(m => m[1]);
  // Phase 1G keeps a single owner-approved external destination on the
  // homepage: the Free Experience CTA. The footer is navigation and brand only.
  assert.deepEqual(externalBodyLinks, r.page === 'home'
    ? [...accessDestinations, registrationDestination]
    : accessDestinations);
  if (r.page === 'home') {
    assert.doesNotMatch(body, /id="demo"/, 'Homepage no longer composes the Demo section');
    const coverage = body.indexOf('id="coverage"');
    const freeExperience = body.indexOf('id="free-experience"');
    const footer = body.indexOf('<footer');
    assert.ok(coverage > 0 && coverage < freeExperience && freeExperience < footer, 'Free experience CTA order');
    assert.match(body, /<section class="fx-section" id="free-experience"/);
    assert.equal(attr(body, /<a class="fx-cta" href="([^"]*)"/), registrationDestination);
    assert.equal((body.match(/data-register-destination/g) || []).length, 1);
    for (const id of ['C52', 'C53', 'C54', 'C55']) assert.ok(body.includes(`data-claim="${id}"`), `Homepage free experience claim ${id}`);
  } else {
    assert.match(body, /id="demo"/, 'Demo retained on Platform and product routes');
    assert.ok(body.includes('href="#demo"'));
  }
  assert.ok(body.includes(labels[r.locale].accessdesc[3]), 'No invented storefront link');
  assert.doesNotMatch(h, /<form\b|<input\b|<textarea\b|<iframe\b|<video\b|<canvas\b/i);
  for (const m of h.matchAll(/(?:src|href)="(\/assets\/[^"]*)"/g)) assert.ok(fs.existsSync(path.join(distDir, m[1])));
  assert.doesNotMatch(h, /src="https?:|@import|fonts\.google|gtag|analytics\.js/i);
  // The Demo receiving-process disclosure belongs to the Demo section, which
  // the homepage no longer composes; it must remain wherever Demo renders.
  if (r.page === 'home') assert.doesNotMatch(body, /demo-pending/);
  else assert.ok(body.includes(labels[r.locale].pending));
   assert.doesNotMatch(body, /class="placeholder"|class="editorial-image/);
   assert.match(body, /data-screen-product=/);
  for (const id of (['home', 'platform'].includes(r.page) ? ['C06', 'C31', 'C35'] : r.page === 'commercehub' ? ['C31', 'C35'] : r.page === 'affiliate' ? ['C23', 'C24', 'C29', 'C38'] : r.page === 'storefront' ? ['C19', 'C27', 'C30'] : ['C09', 'C13', 'C14', 'C15'])) {
    assert.ok(body.includes(`data-claim="${id}"`), `Required full boundary ${id}`);
  }
});
test('Phase 1G footer: named Explore/Products navigation, exact localized links and no review notes', () => {
  for (const locale of locales) {
    const home = html(routes.find(x => x.page === 'home' && x.locale === locale));
    const footer = home.slice(home.indexOf('<footer'), home.indexOf('</footer>') + 9);
    const links = [...footer.matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g)].map(m => ({ href: attr(m[1], /href="([^"]*)"/), text: plain(m[2]) }));
    assert.equal((footer.match(/<footer\b/g) || []).length, 1);
    assert.equal((footer.match(/<nav\b/g) || []).length, 2);
    assert.equal((footer.match(/<h2\b/g) || []).length, 2);
    assert.ok(footer.includes(`<h2 class="sf-heading" id="sf-explore-title">${labels[locale].footerExplore}</h2>`));
    assert.ok(footer.includes(`<h2 class="sf-heading" id="sf-products-title">${labels[locale].products}</h2>`));
    assert.ok(footer.includes('aria-labelledby="sf-explore-title"') && footer.includes('aria-labelledby="sf-products-title"'));
    assert.equal(links.length, 7);
    assert.equal(links[0].href, `${pagePath(locale, 'home')}#main`);
    assert.deepEqual(links.slice(1).map(l => l.text), [labels[locale].home, labels[locale].platform, ...products.map(p => labels[locale][p.key])]);
    assert.deepEqual(links.slice(1).map(l => l.href), ['home', 'platform', ...products.map(p => p.key)].map(p => `${pagePath(locale, p)}#main`));
    assert.ok(footer.includes(`alt="SHOPYXI — ${labels[locale].home}"`));
    assert.equal((footer.match(/<span class="sf-dot sf-dot-(pos|storefront|affiliate|commercehub)" aria-hidden="true"><\/span>/g) || []).length, 4);
    const images = [...footer.matchAll(/<img\b[^>]*>/g)].map(m => m[0]);
    assert.equal(images.length, 2);
    assert.ok(images.every(i => i.includes('src="/assets/shopyxi-logo.png"')));
    assert.match(images[1], /alt=""/);
    assert.doesNotMatch(footer, /admin\.shopyxi\.com\/register|href="#demo"/);
    assert.doesNotMatch(footer, /About is deferred|Owner approval required|Internal marketing review/);
    for (const phrase of [labels[locale].company, labels[locale].footer, labels[locale].gate]) assert.ok(!footer.includes(phrase));
  }
  for (const locale of locales) for (const page of ['platform', 'pos', 'storefront', 'affiliate', 'commercehub']) {
    const review = html(routes.find(x => x.page === page && x.locale === locale));
    const footer = review.slice(review.indexOf('<footer'), review.indexOf('</footer>') + 9);
    assert.equal((footer.match(/href="#demo"/g) || []).length, 1, `${locale}/${page} keeps one review Demo link`);
    assert.ok(footer.includes(claims.C39[locale]));
    assert.doesNotMatch(footer, /admin\.shopyxi\.com\/register/);
  }
});
test('unique localized titles/descriptions and exact immutable artwork', () => {
  assert.equal(new Set(routes.map(r => attr(html(r), /<title>(.*?)<\/title>/))).size, 12);
  assert.equal(new Set(routes.map(r => attr(html(r), /<meta name="description" content="([^"]*)"/))).size, 12);
  const original = fs.readFileSync(path.join(root, 'docs/branding/assets/SHOPYXI_primary_logo_transparent.png'));
  assert.equal(sha(original), sha(fs.readFileSync(path.join(distDir, 'assets/shopyxi-logo.png'))));
  assert.equal(original.readUInt32BE(16), 2400); assert.equal(original.readUInt32BE(20), 747);
});
test('inherited fragment/disclosure wording resolves to Phase 2 claim addendum', () => {
  const addendum = fs.readFileSync(path.join(root, 'docs/marketing/phase-2-design-20261121-01/SHOPYXI-MARKETING-CLAIMS-REGISTER-PHASE-2-ADDENDUM.md'), 'utf8');
  for (const locale of locales) {
    for (const text of labels[locale].accessdesc) assert.ok(addendum.includes(text));
    for (const id of ['pending', 'reserved', 'placeholder', 'proofnote']) assert.ok(addendum.includes(labels[locale][id]));
    for (const id of ['operational', 'shopping', 'promotion', 'selling', 'merchant', 'supportedstore', 'eligiblehub', 'eligibleproduct', 'link', 'attributed', 'accounts', 'orders', 'commissions']) {
      assert.ok(addendum.includes(labels[locale][id]), `Registered fragment ${locale}/${id}`);
    }
  }
});
test('no runtime APIs, tracking, storage, authentication, external fonts or POS tokens/styles', () => {
  const js = read('src/client.js');
  assert.doesNotMatch(js, /\bfetch\s*\(|XMLHttpRequest|WebSocket|sendBeacon|localStorage|sessionStorage|document\.cookie|navigator\.(language|geolocation)/);
  const css = ['fonts', 'tokens', 'marketing', 'implementation', 'enrichment'].map(n => read(`src/styles/${n}.css`)).join('\n');
  assert.doesNotMatch(css, /--pos-|@import|overflow-x\s*:\s*hidden|url\(["']?https?:/);
  assert.equal((read('src/styles/fonts.css').match(/@font-face/g) || []).length, 3);
  assert.equal((read('src/styles/fonts.css').match(/font-display: swap/g) || []).length, 3);
  assert.equal((read('src/styles/fonts.css').match(/font-weight: 200 1000/g) || []).length, 3);
  assert.match(read('public/assets/Cairo-OFL.txt'), /SIL OPEN FONT LICENSE Version 1.1/);
  assert.match(css, /prefers-reduced-motion/);
  assert.ok(receipt.sizes.find(x => x.file.endsWith('.js')).bytes < 3000);
  assert.doesNotMatch(readDist(receipt.assets.js.slice(1)), /react-dom|hydrateRoot|createRoot/);
});
test('local Cairo and genuine cropped captures are bundled without historical artwork', () => {
  for (const subset of ['arabic', 'latin', 'latin-ext']) {
    const bytes = fs.readFileSync(path.join(distDir, `assets/cairo-${subset}-variable.woff2`));
    assert.equal(bytes.subarray(0, 4).toString(), 'wOF2');
  }
  for (const r of routes) {
    const h = html(r);
    assert.match(h, /rel="preload" href="\/assets\/cairo-latin-variable.woff2"/);
    assert.equal(h.includes('rel="preload" href="/assets/cairo-arabic-variable.woff2"'), r.locale === 'ar');
     assert.match(h, /src="\/assets\/screen-/);
     assert.doesNotMatch(h, /\/assets\/editorial-/);
    assert.match(h, /loading="lazy"/);
    assert.match(h, /fetchpriority="high"/);
  }
   assert.ok(receipt.generatedFiles.every(f => !f.includes('-master') && !f.includes('editorial-')));
});
test('review robots/sitemap and fail-closed publication command do not change build', () => {
  const gates = JSON.parse(read('publication-gates.json'));
  assert.equal(gates.releaseState, 'PUBLIC_APPROVED_LEAN'); assert.equal(gates.phase4Authorized, true);
  assert.match(readDist('robots.txt'), /Disallow: \//); assert.doesNotMatch(readDist('robots.txt'), /Sitemap:/);
  assert.match(readDist('sitemap.xml'), /<urlset xmlns="http:\/\/www.sitemaps.org\/schemas\/sitemap\/0.9"><\/urlset>/);
  assert.doesNotMatch(readDist('sitemap.xml'), /<loc>|127\.0\.0\.1|localhost/);
  const before = sha(readDist('build-receipt.json'));
  if (gates.publicReleaseBlocked || gates.gates.some(g => g.required && g.status !== 'APPROVED')) {
    const publicBuild = spawnSync(process.execPath, ['scripts/build.mjs', '--public'], { cwd: app, encoding: 'utf8' });
    assert.equal(publicBuild.status, 1); assert.match(publicBuild.stderr, /publicReleaseBlocked/);
  }
  assert.equal(sha(readDist('build-receipt.json')), before);
});