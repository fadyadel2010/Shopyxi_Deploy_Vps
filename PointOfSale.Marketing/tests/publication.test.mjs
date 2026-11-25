import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { publicClaim } from '../src/publication.mjs';
import { releaseInputs } from '../scripts/release-inputs.mjs';
const app = new URL('../', import.meta.url);
const claims = JSON.parse(await fs.readFile(new URL('src/content/claims.json', app)));
const policy = JSON.parse(await fs.readFile(new URL('src/content/publication.json', app)));

test('historical pending register and public evidence subset are distinct and exhaustive', () => {
  assert.ok(Object.values(claims).every(c => c.approval === 'PENDING'));
  assert.deepEqual([...policy.supported, ...policy.excluded].sort(), Object.keys(claims).sort());
  assert.equal(new Set([...policy.supported, ...policy.excluded]).size, Object.keys(claims).length);
  for (const id of policy.excluded) assert.throws(() => publicClaim(claims, id, 'en'));
  for (const id of policy.supported) for (const locale of ['en', 'ar']) assert.ok(publicClaim(claims, id, locale));
  for (const locale of ['en', 'ar']) {
    assert.doesNotMatch(publicClaim(claims, 'C53', locale) + publicClaim(claims, 'C55', locale), /free|مجاني/i);
    assert.doesNotMatch(publicClaim(claims, 'C15', locale), /branch|team|فروع|فريق/i);
    assert.equal(publicClaim(claims, 'C27', locale), claims.C27a[locale]);
  }
});

test('qualification binds build scripts, tests, assets, lockfile, runtime and deployment packaging', async () => {
  const { fileURLToPath } = await import('node:url');
  const inputs = await releaseInputs(fileURLToPath(app));
  for (const file of ['scripts/build.mjs', 'tests/public.mjs', 'package-lock.json',
    'nginx.conf', 'Dockerfile', 'container/build-image.py', 'src/content/publication.json']) {
    assert.match(inputs.manifest[file], /^[a-f0-9]{64}$/);
  }
});

test('pinned nonroot multistage packaging preserves narrow security/routing contract', async () => {
  const docker = await fs.readFile(new URL('Dockerfile', app), 'utf8');
  const nginx = await fs.readFile(new URL('nginx.conf', app), 'utf8');
  assert.equal((docker.match(/^FROM .*@sha256:[a-f0-9]{64}/gm) || []).length, 2);
  assert.match(docker, /RUN npm ci --no-audit --no-fund/);
  assert.match(docker, /RUN npm run build:public/);
  assert.match(docker, /USER 101:101/);
  assert.match(docker, /COPY --from=public --chown=101:101 \/app\/\.build\/container-site\/ \/site\//);
  for (const header of ['Content-Security-Policy', 'X-Content-Type-Options', 'X-Frame-Options',
    'Referrer-Policy', 'Permissions-Policy', 'Strict-Transport-Security']) {
    assert.match(nginx, new RegExp(`add_header ${header} .* always;`));
  }
  assert.match(nginx, /connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'/);
  assert.match(nginx, /listen 8081;[\s\S]*location = \/healthz/);
  assert.match(nginx, /if \(\$host != shopyxi.com\) \{ return 444; \}/);
  assert.match(nginx, /if \(\$marketing_file = ""\) \{ return 404; \}/);
  assert.match(nginx, /no-transform/);
});
