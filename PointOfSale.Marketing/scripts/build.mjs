import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { gzipSync, brotliCompressSync } from 'node:zlib';
import { build, transform } from 'esbuild';
import { routes } from '../src/routes.mjs';
import { verifyScreenshots } from './screenshots.mjs';
import { releaseInputs } from './release-inputs.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
// MARKETING_DIST lets an isolated review/preview build be produced outside the
// committed dist/ output. Defaults to dist/ for the normal workflow.
const dist = path.resolve(process.env.MARKETING_DIST || path.join(app, 'dist'));
const gates = JSON.parse(await fs.readFile(path.join(app, 'publication-gates.json'), 'utf8'));
const publicRelease = process.argv.includes('--public');
let publicInputBinding;
if (publicRelease) {
  const inputs = await releaseInputs(app);
  publicInputBinding = { sourceSha256: inputs.sourceSha256, inputsSha256: inputs.inputsSha256 };
  const pending = gates.gates.filter(g => g.required && g.status !== 'APPROVED').map(g => g.id);
  const approval = JSON.parse(await fs.readFile(path.join(app, 'release-approval.json'), 'utf8'));
  const contentPaths = [
    ...(await fs.readdir(path.join(app, 'src/content'))).filter(n => n.endsWith('.json')).map(n => `src/content/${n}`),
    'src/components/editorial.jsx',
    'src/components/product-screen.jsx',
    ...(await fs.readdir(path.join(app, 'public/assets'))).map(n => `public/assets/${n}`)
  ].sort();
  const manifest = {};
  for (const file of contentPaths) manifest[file] = createHash('sha256').update(await fs.readFile(path.join(app, file))).digest('hex');
  const approvedContentSha256 = createHash('sha256').update(JSON.stringify(manifest)).digest('hex');
  let technical = {};
  try { technical = JSON.parse(await fs.readFile(path.join(app, 'release-qualification.json'), 'utf8')); }
  catch (e) { if (e.code !== 'ENOENT') throw e; }
  const sourceSha256 = inputs.sourceSha256;
  const technicalQualified = technical.pass === true && technical.sourceSha256 === sourceSha256 &&
    technical.inputsSha256 === inputs.inputsSha256 &&
    technical.failedStates === 0 && technical.failedAssertions === 0 &&
    /^[a-f0-9]{64}$/.test(technical.browserReportSha256 || '') &&
    ['chromium', 'firefox', 'webkit'].every(engine => technical.engines?.includes(engine)) &&
    technical.states === 180 && technical.axeAudits === 72 && technical.errors === 0 &&
    technical.consoleErrors === 0 && technical.externalRequests === 0 && technical.axeViolations === 0 &&
    technical.axeIncompleteAudits === 0 && technical.brokenResponses === 0;
  const screenshotsQualified = await verifyScreenshots(app).catch(() => false);
  if (pending.length || gates.publicReleaseBlocked || !gates.phase4Authorized ||
      gates.releaseState !== 'PUBLIC_APPROVED_LEAN' ||
      !technicalQualified || !screenshotsQualified ||
       approvedContentSha256 !== approval.approvedContentSha256 ||
       sourceSha256 !== approval.publicSourceSha256 ||
      approval.scope.demoSubmission !== false || approval.scope.leadCollection !== false ||
      approval.scope.productScreenshots !== true || approval.scope.aboutCompanyFacts !== false) {
    console.error(JSON.stringify({ publicReleaseBlocked: true, pending, technicalQualified, screenshotsQualified, approvedContentMatches: approvedContentSha256 === approval.approvedContentSha256,
      reason: 'Public release requires actual scoped approval, exact approved content and all required gates. No public build produced.' }, null, 2));
    process.exit(1);
  }
}
await fs.mkdir(path.join(app, '.build'), { recursive: true });
await build({
  entryPoints: [path.join(app, 'src/App.jsx')],
  outfile: path.join(app, '.build/render.cjs'),
  bundle: true, platform: 'node', format: 'cjs', packages: 'external',
  jsx: 'transform', logLevel: 'warning'
});
const require = createRequire(import.meta.url);
const { render, renderRoot } = require(path.join(app, '.build/render.cjs'));
// Remove only files named by our own previous output receipt. No broad cleanup.
try {
  const prior = JSON.parse(await fs.readFile(path.join(dist, 'build-receipt.json'), 'utf8'));
  for (const relative of prior.generatedFiles) {
    const target = path.resolve(dist, relative);
    if (!target.startsWith(dist + path.sep)) throw Error('Invalid prior output path');
    await fs.unlink(target).catch(e => { if (e.code !== 'ENOENT') throw e; });
  }
} catch (e) { if (e.code !== 'ENOENT') throw e; }
const generatedFiles = [];
async function output(relative, content) {
  const target = path.join(dist, relative);
  await fs.mkdir(path.dirname(target), { recursive: true });
  await fs.writeFile(target, content);
  generatedFiles.push(relative);
}
const cssSource = (await Promise.all(['fonts', 'tokens', 'marketing', 'implementation', 'enrichment', 'composition', 'homepage', 'product-showcase', 'ecosystem-relationships', 'business-capabilities-showcase', 'capabilities-coverage', 'free-experience-cta', 'footer'].map(name => fs.readFile(path.join(app, `src/styles/${name}.css`), 'utf8')))).join('\n');
const css = (await transform(cssSource, { loader: 'css', minify: true, target: ['chrome100', 'firefox100', 'safari15.4'] })).code;
const js = (await transform(await fs.readFile(path.join(app, 'src/client.js'), 'utf8'), { minify: true, target: 'es2020' })).code;
const hash = data => createHash('sha256').update(data).digest('hex');
const assets = { css: `/assets/marketing-${hash(css).slice(0, 12)}.css`, js: `/assets/navigation-${hash(js).slice(0, 12)}.js` };
await output(assets.css.slice(1), css);
await output(assets.js.slice(1), js);
const logo = await fs.readFile(path.join(app, 'public/assets/shopyxi-logo.png'));
const provenance = JSON.parse(await fs.readFile(path.join(app, 'src/content/provenance.json'), 'utf8'));
if (hash(logo) !== provenance.logo.sha256) throw Error('Official artwork bytes changed');
await output('assets/shopyxi-logo.png', logo);
// Only referenced captures ship. Historical captures/artwork stay source-only.
const screenshotManifest = JSON.parse(await fs.readFile(path.join(app, 'src/content/product-screenshots.json'), 'utf8'));
const screenshotAssets = new Set(Object.values(screenshotManifest.products).flatMap(product =>
  Object.values(product).flatMap(screen => ['desktop', 'mobile', 'folio'].map(variant => screen[variant]?.publicPath?.split('/').at(-1)).filter(Boolean))));
for (const name of (await fs.readdir(path.join(app, 'public/assets'))).sort()) {
  if (screenshotAssets.has(name) || /^(cairo-[a-z-]+\.woff2|Cairo-OFL\.txt)$/.test(name))
    await output(`assets/${name}`, await fs.readFile(path.join(app, 'public/assets', name)));
}
for (const route of routes) await output(`${route.path.slice(1).replace(/\/$/, '')}/index.html`, render(route, assets, publicRelease));
await output('index.html', renderRoot(assets, publicRelease));
await output('404.html', '<!doctype html><html lang="en" dir="ltr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,nofollow"><title>SHOPYXI — 404</title></head><body><main><h1>404</h1><a href="/">English / العربية</a></main></body></html>');
await output('robots.txt', publicRelease ? 'User-agent: *\nAllow: /\nSitemap: https://shopyxi.com/sitemap.xml\n' : '# INTERNAL REVIEW ONLY — not publication authorized\nUser-agent: *\nDisallow: /\n');
// Valid empty review sitemap: no crawl invitation and no review-host URLs.
await output('sitemap.xml', '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">' +
  (publicRelease ? routes.map(r => r.path).map(p => `<url><loc>https://shopyxi.com${p}</loc></url>`).join('') : '') + '</urlset>\n');
await output('publication-gates.json', JSON.stringify(gates, null, 2) + '\n');
await output('routes.json', JSON.stringify(routes, null, 2) + '\n');
const sizes = await Promise.all(generatedFiles.map(async file => {
  const bytes = await fs.readFile(path.join(dist, file));
  return { file, bytes: bytes.length, gzip: gzipSync(bytes).length, brotli: brotliCompressSync(bytes).length, sha256: hash(bytes) };
}));
const receipt = {
  mode: publicRelease ? 'public-lean' : 'review', optimizedStaticBuild: true, publicReleaseBlocked: !publicRelease, node: process.version,
  ...publicInputBinding,
  routes: routes.length, assets, generatedFiles: [...generatedFiles, 'build-receipt.json'],
  totalBytes: sizes.reduce((n, x) => n + x.bytes, 0),
  browserRuntime: 'Only navigation enhancement; React, ReactDOM, Lucide package code never shipped.',
  sizes
};
await output('build-receipt.json', JSON.stringify(receipt, null, 2) + '\n');
console.log(JSON.stringify(receipt, null, 2));
