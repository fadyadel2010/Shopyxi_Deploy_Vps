/** A preview must never infer trusted authority from the shopper-controlled URL. */
export function resolveTrustedAdminOrigin(value: string | undefined, development: boolean): string {
    if (!value && development) return 'http://localhost:5173';
    if (!value) throw new Error('VITE_ADMIN_ORIGIN is required for the authenticated draft preview');
    let url: URL;
    try { url = new URL(value); } catch { throw new Error('VITE_ADMIN_ORIGIN must be an exact origin'); }
    if (url.username || url.password || url.hostname.includes('*') || url.pathname !== '/' || url.search || url.hash
        || (url.protocol !== 'https:' && !(development && url.protocol === 'http:' && url.hostname === 'localhost'))) {
        throw new Error('VITE_ADMIN_ORIGIN must be an exact HTTPS origin (localhost HTTP is development-only)');
    }
    return url.origin;
}
