/**
 * API Authentication Middleware
 * 
 * SECURITY: This middleware protects all Baileys API endpoints from unauthorized access.
 * Without this, any process that can reach the service could send WhatsApp messages as any tenant.
 * 
 * Authentication is performed via X-API-Key header validation.
 * The /health endpoint is exempt to allow load balancer health checks.
 */

import type { Request, Response, NextFunction } from 'express';
import { config } from '../config';
import { createLogger } from '../utils/logger';
import { createHash, timingSafeEqual } from 'node:crypto';

const logger = createLogger('AuthMiddleware');

/**
 * List of paths that do not require authentication.
 * 
 * Exempt paths:
 * - /health - Load balancer health checks
 * - /ready  - Kubernetes readiness probe
 * - /live   - Kubernetes liveness probe
 * - /metrics - Prometheus metrics scraping
 */
const PUBLIC_PATHS = ['/health', '/ready', '/live', '/metrics'];

/**
 * Timing-safe string comparison to prevent timing attacks.
 */
function apiKeysEqual(a: string, b: string): boolean {
    // Fixed-size hashes avoid a JavaScript comparison loop and length mismatch
    // exceptions. Neither supplied key nor digest is retained or logged.
    return timingSafeEqual(createHash('sha256').update(a, 'utf8').digest(),
        createHash('sha256').update(b, 'utf8').digest());
}

/**
 * API Key authentication middleware.
 * 
 * - Validates X-API-Key header against configured BAILEYS_API_KEY
 * - Skips authentication for public paths (health check)
 * - In development mode with no API key set, allows all requests (with warning)
 * - Uses timing-safe comparison to prevent timing attacks
 */
export function apiKeyAuth(req: Request, res: Response, next: NextFunction): void {
    // Skip auth for public paths
    if (PUBLIC_PATHS.includes(req.path)) {
        return next();
    }

    // Express routes are case-insensitive by default. The credential boundary
    // must use the same semantics, including /ADMIN and mixed-case prefixes.
    const admin = config.nodeEnv === 'production' && /^\/admin(?:\/|$)/i.test(req.path);
    const configuredApiKey = admin ? config.security.adminApiKey : config.security.apiKey;
    // The normal API transport has no production admin/reset/diagnostic authority.
    // An absent dedicated admin key disables these routes; no implicit fallback.
    if (admin && !configuredApiKey) {
        res.status(403).json({ success: false, error: 'Administrative access is disabled' });
        return;
    }

    // Development mode without API key - allow but warn
    if (!configuredApiKey && config.nodeEnv !== 'production') {
        logger.warn({ path: req.path }, 'API key not configured - allowing request in development mode');
        return next();
    }

    // Production mode without API key should never happen (blocked by validateConfig)
    // but we double-check here for defense in depth
    if (!configuredApiKey) {
        logger.error('API key not configured in production - rejecting request');
        res.status(500).json({
            success: false,
            error: 'Server configuration error'
        });
        return;
    }

    // Get API key from header
    const providedApiKey = req.headers['x-api-key'];

    if (typeof providedApiKey !== 'string' || !providedApiKey || providedApiKey.length > 8192) {
        logger.warn({ path: req.path, ip: req.ip }, 'Missing API key header');
        res.status(401).json({
            success: false,
            error: 'Unauthorized - missing API key'
        });
        return;
    }

    // Timing-safe comparison
    if (!apiKeysEqual(providedApiKey, configuredApiKey)) {
        logger.warn({ path: req.path, ip: req.ip }, 'Invalid API key provided');
        res.status(401).json({
            success: false,
            error: 'Unauthorized - invalid API key'
        });
        return;
    }

    // API key valid
    next();
}
