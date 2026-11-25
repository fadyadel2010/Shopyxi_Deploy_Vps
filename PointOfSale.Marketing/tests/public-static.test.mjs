import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { routes, siteOrigin, accessDestinations } from '../src/routes.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = file => fs.readFileSync(path.join(app, 'dist', file), 'utf8');
const receipt = JSON.parse(read('build-receipt.json'));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');

test('public artifact requires actual approved gates and source-bound three-engine qualification', () => {
  assert.equal(receipt.mode, 'public-lean');
  assert.equal(receipt.publicReleaseBlocked, false);
  const gates = JSON.parse(fs.readFileSync(path.join(app, 'publication-gates.json')));
  assert.ok(gates.gates.filter(g => g.required).every(g => g.status === 'APPROVED'));
  const qualification = JSON.parse(fs.readFileSync(path.join(app, 'release-qualification.json')));
  assert.equal(qualification.pass, true);
  assert.deepEqual(qualification.engines, ['chromium', 'firefox', 'webkit']);
});
for (const route of routes) test(`${route.path}: public content, working scope and metadata`, () => {
  const html = read(route.path.slice(1).replace(/\/$/, '') + '/index.html');
  assert.match(html, /data-public-release="lean"/);
  assert.match(html, /name="robots" content="index, follow"/);
  assert.ok(html.includes(`<link rel="canonical" href="${siteOrigin}${route.path}"`));
  assert.equal((html.match(/<h1\b/g) || []).length, 1);
  assert.doesNotMatch(html, /data-review-only|data-approval="PENDING"|href="#demo"|id="demo"|class="placeholder"|<form\b|<input\b|<textarea\b|<button\b/);
  const body = html.slice(html.indexOf('<body'));
  assert.deepEqual([...body.matchAll(/href="(https:[^"]*)"/g)].map(m => m[1]), accessDestinations);
  const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(m => m[1]);
  for (const m of html.matchAll(/href="#([^"]+)"/g)) assert.ok(ids.includes(m[1]), m[1]);
  for (const locale of ['en', 'ar']) assert.ok(html.includes(`hrefLang="${locale}"`) || html.includes(`hreflang="${locale}"`));
});
test('public robots and sitemap advertise only the 12 nonredirecting bilingual entries', () => {
  assert.match(read('robots.txt'), /Allow: \//);
  assert.match(read('robots.txt'), /Sitemap: https:\/\/shopyxi.com\/sitemap.xml/);
  assert.doesNotMatch(read('robots.txt'), /Disallow:/);
  const locations = [...read('sitemap.xml').matchAll(/<loc>([^<]+)<\/loc>/g)].map(m => m[1]);
  assert.deepEqual(locations, routes.map(r => siteOrigin + r.path));
});
test('all built assets match the exact receipt, with no artwork masters', () => {
  for (const item of receipt.sizes) {
    assert.equal(sha(fs.readFileSync(path.join(app, 'dist', item.file))), item.sha256);
    assert.doesNotMatch(item.file, /master/);
  }
});
