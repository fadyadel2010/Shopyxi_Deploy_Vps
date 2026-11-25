import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
fs.mkdirSync(evidence, { recursive: true });
const commands = [
  ['build', ['scripts/build.mjs']],
  ['static-tests', ['--test', 'tests/static.test.mjs']],
  ['browser', ['tests/browser.mjs']]
];
const results = [];
for (const [name, args] of commands) {
  const started = new Date().toISOString();
  const result = spawnSync(process.execPath, args, { cwd: app, env: { ...process.env, MARKETING_EVIDENCE_DIR: evidence }, encoding: 'utf8', maxBuffer: 50 * 1024 * 1024 });
  fs.writeFileSync(path.join(evidence, `${name}.txt`), `$ node ${args.join(' ')}\n${result.stdout || ''}${result.stderr || ''}`);
  results.push({ name, command: ['node', ...args], started, finished: new Date().toISOString(), exitCode: result.status, error: result.error?.message });
  console.log(`${name}: ${result.status === 0 ? 'PASS' : 'FAIL'} (${path.join(evidence, `${name}.txt`)})`);
  if (name === 'build' && result.status !== 0) break;
}
const pass = results.length === 3 && results.every(x => x.exitCode === 0);
fs.writeFileSync(path.join(evidence, 'commands.json'), JSON.stringify({ pass, results }, null, 2) + '\n');
if (!pass) process.exitCode = 1;
