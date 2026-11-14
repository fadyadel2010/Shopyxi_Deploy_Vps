export function resolveStorefrontReturnPath(value: string | null | undefined, fallback = '/'): string {
    if (typeof value !== 'string') {
        return fallback;
    }

    const candidate = value.trim();
    if (
        !candidate.startsWith('/') ||
        candidate.startsWith('//') ||
        candidate.includes('\\') ||
        Array.from(candidate).some(char => char.charCodeAt(0) < 32 || char.charCodeAt(0) === 127)
    ) {
        return fallback;
    }

    return candidate;
}
