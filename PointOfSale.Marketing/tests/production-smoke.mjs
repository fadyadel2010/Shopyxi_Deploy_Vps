// GET/HEAD-only live artifact verification; no registrations or business writes.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { routes, csp, rootRedirect } from '../src/routes.mjs';
import { sha256 } from '../scripts/release-inputs.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const origin = process.env.MARKETING_ORIGIN || 'https://shopyxi.com';
const evidence = process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output/live');
const receipt = JSON.parse(await fs.readFile(path.join(app, 'dist/build-receipt.json'), 'utf8'));
if (receipt.mode !== 'public-lean') throw Error('Public artifact required');
const results = { origin, checks: [], startedUtc: new Date().toISOString() };
function check(pass, label, detail) {
  results.checks.push({ pass: !!pass, label, ...(detail ? { detail } : {}) });
}
async function get(url, options) {
  return fetch(url, { redirect: 'manual', signal: AbortSignal.timeout(30000), ...options });
}
try {
  for (const method of ['GET', 'HEAD']) {
    const response = await get(origin + '/', { method });
    check(response.status === 302 && response.headers.get('location') === '/ar/', `${method} root Arabic redirect`);
    check(response.headers.get('cache-control') === rootRedirect.cacheControl, `${method} root no-store/no-transform`);
    check(response.headers.get('x-robots-tag') === 'noindex, follow', `${method} root noindex`);
    await response.arrayBuffer();
  }
  const publicFiles = receipt.sizes.filter(x => !['index.html', '404.html', 'routes.json', 'publication-gates.json'].includes(x.file));
  for (const item of publicFiles) {
    const route = routes.find(r => `${r.path.slice(1).replace(/\/$/, '')}/index.html` === item.file);
    const url = origin + (route ? route.path : '/' + item.file);
    const response = await get(url);
    const bytes = Buffer.from(await response.arrayBuffer());
    check(response.status === 200, `${item.file} status`, String(response.status));
    check(sha256(bytes) === item.sha256, `${item.file} exact artifact SHA-256`, sha256(bytes));
    check(response.headers.get('content-security-policy') === csp + "; frame-ancestors 'none'", `${item.file} CSP`);
    for (const [name, expected] of Object.entries({
      'x-content-type-options': 'nosniff', 'x-frame-options': 'DENY',
      'referrer-policy': 'no-referrer',
      'permissions-policy': 'camera=(), microphone=(), geolocation=(), payment=()'
    })) check(response.headers.get(name) === expected, `${item.file} ${name}`);
    check(response.headers.get('strict-transport-security')?.includes('max-age=31536000'), `${item.file} HSTS`);
    const cache = response.headers.get('cache-control') || '';
    check(route || ['robots.txt', 'sitemap.xml'].includes(item.file) ? cache.includes('no-transform') && cache.includes('must-revalidate')
      : /marketing-|navigation-/.test(item.file) ? cache.includes('immutable') : cache.includes('must-revalidate'), `${item.file} cache policy`);
    if (route && !route.path.endsWith('/')) {
      const alias = await get(origin + route.path + '/');
      check(alias.status === 200 && sha256(Buffer.from(await alias.arrayBuffer())) === item.sha256, `${route.path}/ alias artifact`);
    }
  }
  for (const pathname of ['/not-a-page', '/assets/not-an-asset.png', '/build-receipt.json', '/publication-gates.json', '/routes.json']) {
    const response = await get(origin + pathname);
    check(response.status === 404, `${pathname} real 404`, String(response.status));
    await response.arrayBuffer();
  }
  const health = await get(origin + '/healthz');
  check(health.status === 403, 'Public health remains private under unchanged Traefik rule');
  await health.arrayBuffer();
  const registration = await get('https://admin.shopyxi.com/register');
  check(registration.status === 200, 'Existing registration destination reachable by GET, no submission');
  await registration.arrayBuffer();
} catch (error) {
  check(false, 'Verification exception', error.stack);
} finally {
  results.completedUtc = new Date().toISOString();
  results.pass = results.checks.length > 300 && results.checks.every(x => x.pass);
  await fs.mkdir(evidence, { recursive: true });
  await fs.writeFile(path.join(evidence, 'production-smoke.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify({ pass: results.pass, checks: results.checks.length, failures: results.checks.filter(x => !x.pass) }, null, 2));
  if (!results.pass) process.exitCode = 1;
}
