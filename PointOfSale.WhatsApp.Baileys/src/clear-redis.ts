// Script to clear all WhatsApp auth states from Redis
import { createBaileysRedisClient, closeBaileysRedisClient } from './redis/RedisConnection';
import { config } from './config';

async function clearAuthStates() {
    const redis = createBaileysRedisClient(config.redis, config.nodeEnv === 'production');

    await redis.connect();
    console.log('Connected to Redis');

    // Find all WhatsApp auth keys
    const authKeys = await redis.keys('whatsapp:auth:*');
    const sessionKeys = await redis.keys('whatsapp:session:*');

    console.log('Found auth key count:', authKeys.length);
    console.log('Found session key count:', sessionKeys.length);

    // Delete all auth states
    for (const key of authKeys) {
        await redis.del(key);
        console.log('Deleted an auth state entry');
    }

    // Delete all session metadata
    for (const key of sessionKeys) {
        await redis.del(key);
        console.log('Deleted a session metadata entry');
    }

    console.log('All WhatsApp auth states and sessions cleared!');
    await closeBaileysRedisClient(redis);
}

clearAuthStates().catch(() => {
    console.error('Auth-state cleanup failed. Check Redis credentials, trust and operator access; values are not reported.');
    process.exitCode = 1;
});
