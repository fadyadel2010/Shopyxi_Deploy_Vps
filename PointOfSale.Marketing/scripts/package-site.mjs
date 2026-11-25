import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256 } from './release-inputs.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const receipt = JSON.parse(await fs.readFile(path.join(app, 'dist/build-receipt.json'), 'utf8'));
if (receipt.mode !== 'public-lean' || receipt.publicReleaseBlocked) throw Error('A guarded public build is required');
const output = path.join(app, '.build/container-site');
await fs.mkdir(output, { recursive: true });
// Start with an absent directory: never allow stale files into a runtime image.
if ((await fs.readdir(output)).length) throw Error('Container staging directory must be empty');
for (const entry of receipt.sizes) {
  if (['routes.json', 'publication-gates.json'].includes(entry.file)) continue;
  const relative = entry.file;
  if (relative.includes('..') || path.isAbsolute(relative)) throw Error('Unsafe receipt path');
  const bytes = await fs.readFile(path.join(app, 'dist', relative));
  if (sha256(bytes) !== entry.sha256) throw Error(`Artifact changed: ${relative}`);
  const target = path.join(output, relative);
  await fs.mkdir(path.dirname(target), { recursive: true });
  await fs.writeFile(target, bytes);
}
console.log(`Verified runtime-only public site: ${output}`);
