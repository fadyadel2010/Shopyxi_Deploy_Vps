// Prove build independence from prior docs/admin source, using existing dependencies only.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
await fs.mkdir(evidence, { recursive: true });
await fs.mkdir(path.join(app, '.build'), { recursive: true });
const sandbox = await fs.mkdtemp(path.join(app, '.build', 'independent-'));
for (const name of ['src', 'scripts', 'public', 'package.json', 'publication-gates.json']) {
  await fs.cp(path.join(app, name), path.join(sandbox, name), { recursive: true, errorOnExist: true });
}
const productionEnv = { ...process.env, NODE_ENV: 'production' };
const production = spawnSync(process.execPath, ['scripts/build.mjs'], { cwd: app, env: productionEnv, encoding: 'utf8' });
const isolated = spawnSync(process.execPath, ['scripts/build.mjs'], { cwd: sandbox, env: productionEnv, encoding: 'utf8' });
const receipt = JSON.parse(await fs.readFile(path.join(app, 'dist/build-receipt.json'), 'utf8'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const files = [];
if (production.status === 0 && isolated.status === 0) {
  for (const file of receipt.generatedFiles) {
    const a = await fs.readFile(path.join(app, 'dist', file));
    const b = await fs.readFile(path.join(sandbox, 'dist', file));
    files.push({ file, identical: digest(a) === digest(b), sha256: digest(a) });
  }
}
const finalBrowser = process.env.MARKETING_BROWSER_RECEIPT;
let validatedOutputMatches = null;
if (finalBrowser) {
  const text = await fs.readFile(finalBrowser, 'utf8');
  const validationBuild = JSON.parse(text.slice(text.indexOf('{')));
  validatedOutputMatches = validationBuild.sizes.every(entry => receipt.sizes.find(x => x.file === entry.file)?.sha256 === entry.sha256);
}
const result = {
  productionBuildExitCode: production.status, isolatedBuildExitCode: isolated.status,
  copiedInputs: ['src', 'scripts', 'public', 'package.json', 'publication-gates.json'],
  dependencyMode: 'Normal Node resolution to pre-existing installed modules; no install, network, POS source, previous-phase docs or services.',
  isolatedPackage: path.relative(app, sandbox).replaceAll('\\', '/'),
  validatedOutputMatches, files,
  pass: production.status === 0 && isolated.status === 0 && files.length === receipt.generatedFiles.length && files.every(x => x.identical) && validatedOutputMatches !== false
};
await fs.writeFile(path.join(evidence, 'independent-production-build.json'), JSON.stringify(result, null, 2) + '\n');
await fs.writeFile(path.join(evidence, 'independent-production-build.txt'), `NODE_ENV=production — original package\n${production.stdout}${production.stderr}\nNODE_ENV=production — isolated copied package\n${isolated.stdout}${isolated.stderr}`);
console.log(JSON.stringify(result, null, 2));
if (!result.pass) process.exitCode = 1;
