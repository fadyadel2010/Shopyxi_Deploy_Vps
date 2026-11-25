import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

for (const scenario of ['pending-gate', 'blocked-flag', 'altered-content', 'altered-script', 'altered-test', 'altered-nginx', 'altered-lockfile', 'altered-styles', 'demo-enabled', 'missing-qualification', 'altered-screenshot', 'screenshots-unapproved']) {
  test(`public release rejects ${scenario} before output writes`, async () => {
    const sandbox = await fs.mkdtemp(path.join(app, '.build', 'guard-'));
    for (const name of ['scripts', 'src', 'public', 'tests', 'container', 'package.json', 'package-lock.json',
      'nginx.conf', 'Dockerfile', '.dockerignore', '.gitattributes', 'README.md',
      'CLAIMS-EVIDENCE.md', 'publication-gates.json', 'release-approval.json']) {
      await fs.cp(path.join(app, name), path.join(sandbox, name), { recursive: true });
    }
    try { await fs.copyFile(path.join(app, 'release-qualification.json'), path.join(sandbox, 'release-qualification.json')); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    const gatesPath = path.join(sandbox, 'publication-gates.json');
    const gates = JSON.parse(await fs.readFile(gatesPath, 'utf8'));
    for (const gate of gates.gates) if (gate.required) gate.status = 'APPROVED';
    if (scenario === 'pending-gate') gates.gates.find(g => g.required).status = 'PENDING';
    if (scenario === 'blocked-flag') gates.publicReleaseBlocked = true;
    if (['pending-gate', 'blocked-flag'].includes(scenario)) await fs.writeFile(gatesPath, JSON.stringify(gates));
    if (scenario === 'altered-content') await fs.appendFile(path.join(sandbox, 'src/content/claims.json'), '\n');
    const changedFile = { 'altered-script': 'scripts/preview.mjs', 'altered-test': 'tests/public.mjs',
      'altered-nginx': 'nginx.conf', 'altered-lockfile': 'package-lock.json', 'altered-styles': 'src/styles/footer.css' }[scenario];
    if (changedFile) await fs.appendFile(path.join(sandbox, changedFile), '\n');
    if (scenario === 'demo-enabled') {
      const approvalPath = path.join(sandbox, 'release-approval.json');
      const approval = JSON.parse(await fs.readFile(approvalPath, 'utf8'));
      approval.scope.demoSubmission = true;
      await fs.writeFile(approvalPath, JSON.stringify(approval));
    }
    if (scenario === 'screenshots-unapproved') {
      const approvalPath = path.join(sandbox, 'release-approval.json');
      const approval = JSON.parse(await fs.readFile(approvalPath, 'utf8'));
      approval.scope.productScreenshots = false;
      await fs.writeFile(approvalPath, JSON.stringify(approval));
    }
    if (scenario === 'altered-screenshot') {
      const manifest = JSON.parse(await fs.readFile(path.join(sandbox, 'src/content/product-screenshots.json'), 'utf8'));
      await fs.appendFile(path.join(sandbox, 'public', manifest.products.pos.en.desktop.publicPath), 'tampered');
    }
    if (scenario === 'missing-qualification') await fs.unlink(path.join(sandbox, 'release-qualification.json')).catch(e => {
      if (e.code !== 'ENOENT') throw e;
    });
    const result = spawnSync(process.execPath, ['scripts/build.mjs', '--public'], { cwd: sandbox, encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /publicReleaseBlocked/);
    await assert.rejects(fs.stat(path.join(sandbox, 'dist')), { code: 'ENOENT' });
  });
}

test('unchanged isolated public release is accepted (positive control)', async () => {
  const sandbox = await fs.mkdtemp(path.join(app, '.build', 'guard-positive-'));
  for (const name of ['scripts', 'src', 'public', 'tests', 'container', 'package.json', 'package-lock.json',
    'nginx.conf', 'Dockerfile', '.dockerignore', '.gitattributes', 'README.md',
    'CLAIMS-EVIDENCE.md', 'publication-gates.json', 'release-approval.json', 'release-qualification.json']) {
    await fs.cp(path.join(app, name), path.join(sandbox, name), { recursive: true });
  }
  const result = spawnSync(process.execPath, ['scripts/build.mjs', '--public'], { cwd: sandbox, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const receipt = JSON.parse(await fs.readFile(path.join(sandbox, 'dist/build-receipt.json'), 'utf8'));
  assert.equal(receipt.mode, 'public-lean');
  assert.equal(receipt.publicReleaseBlocked, false);
});
