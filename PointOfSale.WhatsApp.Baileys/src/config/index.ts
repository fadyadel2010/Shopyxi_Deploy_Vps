import dotenv from 'dotenv';
import { readRedisConfiguration } from '../redis/RedisConnection';
import { isAbsolute } from 'node:path';

// Production/Test inputs must come only from the explicitly scoped environment.
if (!process.env.NODE_ENV || process.env.NODE_ENV === 'development') dotenv.config();

function positiveInteger(value: string | undefined, fallback: number, maximum: number, name: string): number {
  const result = value === undefined ? fallback : Number(value);
  if (!Number.isInteger(result) || result < 1 || result > maximum) throw new Error(`Invalid ${name}`);
  return result;
}

/**
 * Application configuration
 */
export const config = {
  // Server
  port: positiveInteger(process.env.PORT, 3000, 65535, 'PORT'),
  nodeEnv: process.env.NODE_ENV || 'development',
  tls: {
    certificatePath: process.env.BAILEYS_TLS_CERTIFICATE_PATH,
    privateKeyPath: process.env.BAILEYS_TLS_PRIVATE_KEY_PATH
  },

  // Redis
  redis: readRedisConfiguration(process.env),

  // Session
  session: {
    // AUDIT FIX: Increased from 10 minutes to 24 hours (1440 minutes)
    // Sessions stay connected longer for production use
    // Auth state is preserved in Redis regardless of this setting
    idleTimeoutMinutes: positiveInteger(process.env.SESSION_IDLE_TIMEOUT_MINUTES, 1440, Math.floor(2147483647 / 60000), 'SESSION_IDLE_TIMEOUT_MINUTES'),
    cleanupIntervalMinutes: positiveInteger(process.env.SESSION_CLEANUP_INTERVAL_MINUTES, 30, Math.floor(2147483647 / 60000), 'SESSION_CLEANUP_INTERVAL_MINUTES')
  },

  // Pool
  pool: {
    size: positiveInteger(process.env.POOL_SIZE, 10, 2147483647, 'POOL_SIZE'),
    maxConnectionsPerMember: positiveInteger(process.env.POOL_MAX_CONNECTIONS_PER_MEMBER, 50, 2147483647, 'POOL_MAX_CONNECTIONS_PER_MEMBER')
  },

  // Security
  security: {
    authStateEncryptionSecret: process.env.AUTH_STATE_ENCRYPTION_SECRET || 'change-this-in-production-to-a-secure-random-string',
    apiKey: process.env.BAILEYS_API_KEY || '',
    adminApiKey: process.env.BAILEYS_ADMIN_API_KEY || '',
    // Per-tenant salt prefix for key derivation (prevents rainbow table attacks)
    saltPrefix: process.env.AUTH_SALT_PREFIX || 'whatsapp-auth-v2-'
  },

  // Logging
  logging: {
    level: process.env.LOG_LEVEL || 'info'
  }
};

/**
 * Validate configuration
 */
export function validateConfig(): void {
  if (!['production', 'development', 'test'].includes(config.nodeEnv)) {
    throw new Error('NODE_ENV must be production, development or test');
  }
  if (config.nodeEnv === 'production' && process.env.NODE_TLS_REJECT_UNAUTHORIZED === '0') {
    throw new Error('Production cannot disable Node TLS certificate verification');
  }
  if (config.nodeEnv === 'production') {
    if (!config.tls.certificatePath || !config.tls.privateKeyPath ||
        !isAbsolute(config.tls.certificatePath) || !isAbsolute(config.tls.privateKeyPath)) {
      throw new Error('Production Baileys requires absolute mounted HTTPS certificate/key paths');
    }
    const members = (process.env.POOL_MEMBERS || '').split(',');
    const ids = new Set<string>();
    for (const member of members) {
      const parts = member.split(':');
      const id = parts[0]?.trim();
      const phone = parts[1]?.trim();
      if (parts.length !== 2 || !id || !/^[A-Za-z0-9_-]{1,64}$/.test(id) ||
          !phone || !/^\+[1-9]\d{7,14}$/.test(phone) || ids.has(id)) {
        throw new Error('Production POOL_MEMBERS requires unique member IDs and E.164-format phone numbers');
      }
      ids.add(id);
    }
  }
  if (config.port < 1 || config.port > 65535) {
    throw new Error('Invalid PORT configuration');
  }

  if (config.session.idleTimeoutMinutes < 1) {
    throw new Error('SESSION_IDLE_TIMEOUT_MINUTES must be at least 1');
  }

  if (config.pool.size < 1) {
    throw new Error('POOL_SIZE must be at least 1');
  }

  if (config.nodeEnv === 'production' && config.security.authStateEncryptionSecret === 'change-this-in-production-to-a-secure-random-string') {
    throw new Error('AUTH_STATE_ENCRYPTION_SECRET must be set in production');
  }

  if (config.security.authStateEncryptionSecret.trim().length < 32) {
    throw new Error('AUTH_STATE_ENCRYPTION_SECRET must be at least 32 characters');
  }

  // SECURITY: API key is REQUIRED in production to prevent unauthorized access
  if (config.nodeEnv === 'production' && !config.security.apiKey) {
    throw new Error('BAILEYS_API_KEY must be set in production');
  }

  if (config.nodeEnv === 'production' && config.security.apiKey.trim().length < 32) {
    throw new Error('BAILEYS_API_KEY must be at least 32 characters');
  }
  if (config.nodeEnv === 'production' && (/[\x00-\x1f\x7f]/.test(config.security.apiKey) ||
      (config.security.adminApiKey && (config.security.adminApiKey.trim().length < 32 ||
       /[\x00-\x1f\x7f]/.test(config.security.adminApiKey) || config.security.adminApiKey === config.security.apiKey)))) {
    throw new Error('Production API keys require valid header characters and a distinct strong admin key when admin access is enabled');
  }
}
