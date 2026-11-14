import { createBaileysRedisClient, closeBaileysRedisClient, readRedisConfiguration } from '../RedisConnection';

// No connect(), Redis server, environment-file loading or WhatsApp import.
const secure = () => readRedisConfiguration({
  REDIS_TLS: 'true', REDIS_USERNAME: 'fixture-data', REDIS_PASSWORD: 'fixture-data-only',
  REDIS_SENTINEL_NAME: 'fixture-service',
  REDIS_SENTINEL_NODES: '[{"host":"sentinel.example.invalid","port":26379}]',
  REDIS_SENTINEL_USERNAME: 'fixture-sentinel', REDIS_SENTINEL_PASSWORD: 'fixture-sentinel-only'
});

describe('Unit: secure Redis configuration', () => {
  test('loads the approved exact Redis client family', () => {
    expect(require('redis/package.json').version).toBe('6.3.1');
    expect(require('@redis/client/package.json').version).toBe('6.3.1');
  });

  test.each(['not-a-number', '6379extra', '0', '65536', '1.5'])('rejects invalid port %s', port => {
    expect(() => readRedisConfiguration({ REDIS_PORT: port })).toThrow('Invalid REDIS_PORT');
  });

  test.each(['0', '5', 'bad'])('bounds the primary pool: %s', size => {
    expect(() => readRedisConfiguration({ REDIS_MASTER_POOL_SIZE: size })).toThrow();
  });

  test('does not infer Sentinel credentials from data credentials', () => {
    const settings = secure();
    settings.sentinel.password = undefined;
    expect(() => createBaileysRedisClient(settings, true)).toThrow();
  });

  test('rejects incomplete Sentinel discovery without a direct-endpoint fallback', () => {
    const settings = secure();
    settings.sentinel.name = undefined;
    expect(() => createBaileysRedisClient(settings, true)).toThrow();
  });

  test('does not accept TLS identity options on a plaintext socket', () => {
    expect(() => createBaileysRedisClient(readRedisConfiguration({ REDIS_SERVER_NAME: 'fixture.invalid' }))).toThrow();
  });

  test('requires TLS and a named ACL identity in production', () => {
    expect(() => createBaileysRedisClient(readRedisConfiguration({}), true)).toThrow();
    expect(() => createBaileysRedisClient(readRedisConfiguration({ REDIS_TLS: 'true', REDIS_PASSWORD: 'fixture-only' }), true)).toThrow();
  });

  test('construction and closing an unopened client never start a connection', async () => {
    const client = createBaileysRedisClient(secure(), true);
    client.on('error', () => undefined);
    expect(client.isOpen).toBe(false);
    expect(client.isReady).toBe(false);
    await closeBaileysRedisClient(client);
    expect(client.isOpen).toBe(false);
  });
});
