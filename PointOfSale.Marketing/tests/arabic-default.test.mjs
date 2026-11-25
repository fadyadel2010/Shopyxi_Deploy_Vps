import test from 'node:test';
import assert from 'node:assert/strict';
import { createPreview } from '../scripts/preview.mjs';
import { routes, siteOrigin, rootRedirect } from '../src/routes.mjs';
import fs from 'node:fs/promises';

test('root GET/HEAD always temporarily redirects to Arabic, independent of language/cookies/query', async () => {
  const server = createPreview();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  try {
    for (const method of ['GET', 'HEAD']) for (const language of ['en-US', 'ar', 'fr', '']) {
      const response = await fetch(origin + '/?utm_source=test&lang=en', {
        method, redirect: 'manual', headers: { 'Accept-Language': language, Cookie: 'locale=en' }
      });
      assert.equal(response.status, 302);
      assert.equal(response.headers.get('location'), '/ar/');
      assert.equal(response.headers.get('cache-control'), rootRedirect.cacheControl);
      assert.equal(response.headers.get('x-robots-tag'), 'noindex, follow');
      assert.equal(response.headers.get('set-cookie'), null);
      assert.equal(response.headers.get('vary'), null);
      assert.equal(await response.text(), '');
    }
    const followed = await fetch(origin + '/');
    assert.equal(followed.url, origin + '/ar/');
    assert.match(await followed.text(), /<html lang="ar" dir="rtl">/);
    assert.equal((await fetch(origin + '/', { method: 'POST', redirect: 'manual' })).status, 405);
    for (const route of routes) {
      const response = await fetch(origin + route.path);
      assert.equal(response.status, 200);
      const html = await response.text();
      assert.ok(html.includes(`href="${route.equivalent}#locale"`));
      assert.ok(html.includes(`rel="canonical" href="${siteOrigin}${route.path}"`));
      if (route.page === 'home') assert.match(html, /hrefLang="x-default" href="https:\/\/shopyxi.com\/ar\/"/);
    }
    const nginx = await fs.readFile(new URL('../nginx.conf', import.meta.url), 'utf8');
    assert.match(nginx, /location = \/ \{ return 302 \/ar\/; \}/);
    assert.match(nginx, /absolute_redirect off;/);
    assert.match(nginx, /"\/" "no-store, no-cache, max-age=0, must-revalidate, no-transform"/);
    assert.match(nginx, /add_header X-Robots-Tag \$root_robots always/);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
