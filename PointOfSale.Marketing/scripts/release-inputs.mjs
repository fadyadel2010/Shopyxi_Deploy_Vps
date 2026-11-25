import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export async function releaseInputs(app) {
  const manifest = {};
  async function walk(dir) {
    for (const entry of await fs.readdir(path.join(app, dir), { withFileTypes: true })) {
      const file = `${dir}/${entry.name}`;
      if (entry.isDirectory()) await walk(file);
      else manifest[file] = sha256(await fs.readFile(path.join(app, file)));
    }
  }
  for (const dir of ['src', 'scripts', 'tests', 'public', 'container']) await walk(dir);
  for (const file of ['package.json', 'package-lock.json', 'nginx.conf', 'Dockerfile',
    '.dockerignore', '.gitattributes', 'README.md', 'CLAIMS-EVIDENCE.md',
    'publication-gates.json', 'release-approval.json']) {
    manifest[file] = sha256(await fs.readFile(path.join(app, file)));
  }
  const ordered = Object.fromEntries(Object.entries(manifest).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
  const source = Object.fromEntries(Object.entries(ordered).filter(([f]) => f.startsWith('src/')));
  return { sourceSha256: sha256(JSON.stringify(source)), inputsSha256: sha256(JSON.stringify(ordered)), manifest: ordered };
}
