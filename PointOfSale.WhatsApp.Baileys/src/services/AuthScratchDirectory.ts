import * as fs from 'node:fs';
import * as path from 'node:path';

/** Redis remains authoritative. These files are disposable SDK scratch state. */
export function authScratchDirectory(tenantId: string, production = process.env.NODE_ENV === 'production'): string {
  if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$/.test(tenantId)) {
    throw new Error('Invalid tenant identifier for authentication scratch storage');
  }
  const root = production ? '/tmp/shopixy-whatsapp-auth' : path.resolve('./auth_states');
  fs.mkdirSync(root, { recursive: true, mode: 0o700 });
  if (fs.lstatSync(root).isSymbolicLink()) {
    throw new Error('Authentication scratch storage must not be a symbolic link');
  }
  return path.join(root, tenantId);
}
