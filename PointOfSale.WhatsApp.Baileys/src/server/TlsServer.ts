import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { createPrivateKey, X509Certificate } from 'node:crypto';
import { createServer as createHttpsServer } from 'node:https';
import { createServer as createHttpServer, type RequestListener } from 'node:http';

export interface ServerTlsConfiguration {
  certificatePath?: string;
  privateKeyPath?: string;
}

/** Constructs a server, never listens. Production has no plaintext fallback. */
export function createBaileysServer(listener: RequestListener, production: boolean, settings: ServerTlsConfiguration) {
  if (!settings.certificatePath && !settings.privateKeyPath && !production)
    return createHttpServer(listener);
  try {
    if (!settings.certificatePath || !settings.privateKeyPath ||
        !isAbsolute(settings.certificatePath) || !isAbsolute(settings.privateKeyPath)) throw new Error();
    const cert = readFileSync(settings.certificatePath);
    const key = readFileSync(settings.privateKeyPath);
    const leaf = new X509Certificate(cert);
    const now = Date.now();
    const from = Date.parse(leaf.validFrom);
    const to = Date.parse(leaf.validTo);
    if (leaf.ca || !Number.isFinite(from) || !Number.isFinite(to) || from > now || to <= now ||
        !leaf.checkPrivateKey(createPrivateKey(key)) ||
        (leaf.keyUsage?.length && !leaf.keyUsage.includes('1.3.6.1.5.5.7.3.1') && !leaf.keyUsage.includes('2.5.29.37.0')))
      throw new Error();
    return createHttpsServer({ cert, key, minVersion: 'TLSv1.2', maxVersion: 'TLSv1.3' }, listener);
  } catch {
    // PEM/file/crypto exceptions may disclose input or paths. Do not attach them.
    throw new Error('Baileys HTTPS requires readable absolute certificate/key paths, a current server certificate and a matching private key; values are not reported');
  }
}
