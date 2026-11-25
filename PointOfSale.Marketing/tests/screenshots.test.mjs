import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyScreenshots } from '../scripts/screenshots.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('all product crops have valid hashes, source provenance and viewport-bounded coordinates', async () => {
  assert.equal(await verifyScreenshots(app), true);
  const manifest = JSON.parse(await fs.readFile(path.join(app, 'src/content/product-screenshots.json')));
  const entries = Object.values(manifest.products).flatMap(product => Object.values(product).flatMap(screen =>
    ['desktop', 'mobile', 'folio'].map(key => screen[key]).filter(Boolean)));
  assert.equal(entries.length, 20);
  for (const image of entries) {
    assert.equal(image.locale, image.publicPath.includes('-ar-') ? 'ar' : 'en');
    if (!image.publicPath.includes('-mobile-')) assert.equal(image.dimensions.width / image.dimensions.height, 1.5);
  }
});
