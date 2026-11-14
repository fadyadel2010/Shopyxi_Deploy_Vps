import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { createPrivateKey, createPublicKey, X509Certificate } from 'node:crypto';
import { createClient, createSentinel } from 'redis';

/** Only the existing storage commands, shared by direct and native Sentinel clients. */
export interface RedisCommands {
  readonly isReady: boolean;
  get(key: string): Promise<string | null>;
  set(key: string, value: string, options?: { EX?: number }): Promise<string | null>;
  del(key: string): Promise<number>;
  exists(key: string): Promise<number>;
  incr(key: string): Promise<number>;
  expire(key: string, seconds: number): Promise<number>;
  ping(): Promise<string>;
  multi(): RedisTransaction;
}

export interface RedisTransaction {
  incr(key: string): RedisTransaction;
  expire(key: string, seconds: number): RedisTransaction;
  exec(): Promise<unknown[]>;
}

export interface RedisConfiguration {
  topology: 'direct' | 'sentinel';
  host: string;
  port: number;
  username?: string;
  password?: string;
  db: number;
  tls: boolean;
  caCertificatePath?: string;
  clientCertificatePath?: string;
  clientPrivateKeyPath?: string;
  serverName?: string;
  sentinel: {
    name?: string;
    nodes: Array<{ host: string; port: number }>;
    username?: string;
    password?: string;
    caCertificatePath?: string;
    clientCertificatePath?: string;
    clientPrivateKeyPath?: string;
    serverName?: string;
    masterPoolSize: number;
  };
}

function integer(value: string | undefined, fallback: number, min: number, max: number, name: string): number {
  const n = value === undefined || value === '' ? fallback : Number(value);
  if (!Number.isInteger(n) || n < min || n > max) throw new Error(`Invalid ${name}`);
  return n;
}

export function readRedisConfiguration(env: NodeJS.ProcessEnv): RedisConfiguration {
  if (env.REDIS_TLS && env.REDIS_TLS !== 'true' && env.REDIS_TLS !== 'false')
    throw new Error('REDIS_TLS must be true or false');
  let nodes: unknown;
  try { nodes = JSON.parse(env.REDIS_SENTINEL_NODES || '[]'); }
  catch { throw new Error('Invalid REDIS_SENTINEL_NODES JSON'); }
  if (!Array.isArray(nodes) || nodes.some(n => !n || typeof n.host !== 'string' ||
      !n.host.trim() || !Number.isInteger(n.port) || n.port < 1 || n.port > 65535))
    throw new Error('REDIS_SENTINEL_NODES must be a JSON array of host/port objects');
  if (env.NODE_ENV === 'production' && nodes.length === 0 && !env.REDIS_SENTINEL_NAME && !env.REDIS_HOST?.trim())
    throw new Error('Production direct Redis requires an explicit REDIS_HOST; localhost fallback is not permitted');
  const topology = env.REDIS_TOPOLOGY || (env.NODE_ENV === 'production' ? '' : nodes.length || env.REDIS_SENTINEL_NAME ? 'sentinel' : 'direct');
  if (topology !== 'direct' && topology !== 'sentinel')
    throw new Error('REDIS_TOPOLOGY must explicitly select direct or sentinel; Cluster is not supported');
  if (topology === 'direct' && (nodes.length > 0 || env.REDIS_SENTINEL_NAME || env.REDIS_SENTINEL_USERNAME || env.REDIS_SENTINEL_PASSWORD))
    throw new Error('Direct Redis topology cannot include Sentinel settings');
  if (topology === 'sentinel' && (!env.REDIS_SENTINEL_NAME?.trim() || nodes.length === 0))
    throw new Error('Sentinel Redis topology requires service name and seed nodes');
  return {
    topology,
    host: env.REDIS_HOST || 'localhost',
    port: integer(env.REDIS_PORT, 6379, 1, 65535, 'REDIS_PORT'),
    username: env.REDIS_USERNAME || undefined,
    password: env.REDIS_PASSWORD || undefined,
    db: integer(env.REDIS_DB, 0, 0, 2147483647, 'REDIS_DB'),
    tls: env.REDIS_TLS === 'true',
    caCertificatePath: env.REDIS_CA_CERTIFICATE_PATH || undefined,
    clientCertificatePath: env.REDIS_CLIENT_CERTIFICATE_PATH || undefined,
    clientPrivateKeyPath: env.REDIS_CLIENT_PRIVATE_KEY_PATH || undefined,
    serverName: env.REDIS_SERVER_NAME || undefined,
    sentinel: {
      name: env.REDIS_SENTINEL_NAME || undefined,
      nodes,
      username: env.REDIS_SENTINEL_USERNAME || undefined,
      password: env.REDIS_SENTINEL_PASSWORD || undefined,
      caCertificatePath: env.REDIS_SENTINEL_CA_CERTIFICATE_PATH || undefined,
      clientCertificatePath: env.REDIS_SENTINEL_CLIENT_CERTIFICATE_PATH || undefined,
      clientPrivateKeyPath: env.REDIS_SENTINEL_CLIENT_PRIVATE_KEY_PATH || undefined,
      serverName: env.REDIS_SENTINEL_SERVER_NAME || undefined,
      masterPoolSize: integer(env.REDIS_MASTER_POOL_SIZE, 2, 1, 4, 'REDIS_MASTER_POOL_SIZE')
    }
  };
}

export function tlsOptions(enabled: boolean, caPath?: string, serverName?: string, certificatePath?: string, privateKeyPath?: string) {
  if (!enabled) {
    if (caPath || serverName || certificatePath || privateKeyPath) throw new Error('Redis identity/trust settings require TLS');
    return {};
  }
  let cert: Buffer;
  let key: Buffer;
  let ca: Buffer | undefined;
  try {
    if (!certificatePath || !privateKeyPath || !isAbsolute(certificatePath) || !isAbsolute(privateKeyPath)) throw new Error();
    cert = readFileSync(certificatePath);
    key = readFileSync(privateKeyPath);
    const identity = new X509Certificate(cert);
    const privateKey = createPrivateKey(key);
    if (!identity.checkPrivateKey(privateKey) || !identity.keyUsage?.includes('1.3.6.1.5.5.7.3.2') ||
        Date.parse(identity.validFrom) > Date.now() || Date.parse(identity.validTo) <= Date.now()) throw new Error();
    const publicKey = createPublicKey(privateKey);
    if (!(publicKey.asymmetricKeyType === 'rsa' && (publicKey.asymmetricKeyDetails?.modulusLength || 0) >= 2048) &&
        !(publicKey.asymmetricKeyType === 'ec' && ['prime256v1', 'secp384r1', 'secp521r1'].includes(publicKey.asymmetricKeyDetails?.namedCurve || ''))) throw new Error();
    ca = caPath ? readFileSync(caPath) : undefined;
    if (ca && !new X509Certificate(ca).ca) throw new Error();
  } catch {
    throw new Error('Redis TLS requires readable absolute certificate/key files, a matching strong clientAuth identity, current validity and valid trust; values redacted');
  }
  return {
    tls: true as const,
    rejectUnauthorized: true,
    minVersion: 'TLSv1.2' as const,
    cert, key,
    ...(ca ? { ca } : {}),
    ...(serverName ? { servername: serverName } : {})
    // No checkServerIdentity override: Node verifies the configured/discovered host.
  };
}

/** No socket is opened until connect(). Discovery/recovery belong to node-redis. */
export function createBaileysRedisClient(settings: RedisConfiguration, production: boolean = false) {
  const sentinel = settings.sentinel;
  if (settings.topology !== 'direct' && settings.topology !== 'sentinel')
    throw new Error('Redis topology must be direct or sentinel; Cluster is not supported');
  const useSentinel = settings.topology === 'sentinel';
  if (!useSentinel && (sentinel.nodes.length > 0 || sentinel.name))
    throw new Error('Direct Redis topology conflicts with Sentinel settings');
  if (production && (!settings.tls || !settings.username?.trim() || !settings.password?.trim()))
    throw new Error('Production Redis requires TLS and named data-node ACL credentials');
  const dataOptions = {
    username: settings.username,
    password: settings.password,
    database: settings.db,
    name: 'baileys-data',
    disableOfflineQueue: true,
    socket: { connectTimeout: 5000, ...tlsOptions(settings.tls, settings.caCertificatePath, settings.serverName, settings.clientCertificatePath, settings.clientPrivateKeyPath) }
  };
  if (!useSentinel) return createClient({
    RESP: 2,
    ...dataOptions,
    socket: { ...dataOptions.socket, host: settings.host, port: settings.port }
  });

  if (!sentinel.name || sentinel.nodes.length === 0)
    throw new Error('Redis Sentinel requires both service name and seed nodes');
  if (!settings.tls || !settings.username?.trim() || !settings.password?.trim() || !sentinel.username?.trim() || !sentinel.password?.trim())
    throw new Error('Redis Sentinel requires TLS and separate named data-node/Sentinel ACL credentials');
  if (settings.username === sentinel.username && settings.password === sentinel.password)
    throw new Error('Redis data-node and Sentinel ACL credentials must be separate');
  if (!Number.isInteger(sentinel.masterPoolSize) || sentinel.masterPoolSize < 1 || sentinel.masterPoolSize > 4)
    throw new Error('Redis master pool size must be between 1 and 4');

  return createSentinel({
    RESP: 2,
    name: sentinel.name,
    sentinelRootNodes: sentinel.nodes,
    masterPoolSize: sentinel.masterPoolSize,
    replicaPoolSize: 0,
    reserveClient: false,
    scanInterval: 1000,
    maxCommandRediscovers: 3,
    passthroughClientErrorEvents: true,
    nodeClientOptions: dataOptions,
    sentinelClientOptions: {
      username: sentinel.username,
      password: sentinel.password,
      name: 'baileys-sentinel',
      disableOfflineQueue: true,
      socket: {
        connectTimeout: 5000,
        ...tlsOptions(true, sentinel.caCertificatePath, sentinel.serverName, sentinel.clientCertificatePath, sentinel.clientPrivateKeyPath)
      }
    }
  });
}

export type BaileysRedisClient = ReturnType<typeof createBaileysRedisClient>;

/** Drain normally, but never hang application shutdown on an unavailable node. */
export async function closeBaileysRedisClient(client: BaileysRedisClient): Promise<void> {
  if (!client.isOpen) return;
  let timeout: NodeJS.Timeout | undefined;
  try {
    await Promise.race([
      client.close(),
      new Promise<never>((_, reject) => {
        timeout = setTimeout(() => reject(new Error('Redis shutdown timed out')), 5000);
      })
    ]);
  } catch (error) {
    await client.destroy();
    throw error;
  } finally {
    if (timeout) clearTimeout(timeout);
  }
}
