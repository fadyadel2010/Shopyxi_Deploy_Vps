import { expect, it } from 'vitest';
import { resolveTrustedAdminOrigin } from './trustedAdminOrigin';

it('requires explicit production authority and normalizes only an exact origin', () => {
    expect(() => resolveTrustedAdminOrigin(undefined, false)).toThrow('required');
    expect(resolveTrustedAdminOrigin('https://admin.shopyxi.com/', false)).toBe('https://admin.shopyxi.com');
    expect(resolveTrustedAdminOrigin(undefined, true)).toBe('http://localhost:5173');
});

it.each(['http://admin.shopyxi.com', 'https://admin.shopyxi.com/path', 'https://admin.shopyxi.com?next=evil',
    'https://admin.shopyxi.com#fragment', 'https://user:password@example.com', 'null', 'https://*.shopyxi.com'])('rejects non-origin or insecure production configuration: %s', value => {
    expect(() => resolveTrustedAdminOrigin(value, false)).toThrow();
});
