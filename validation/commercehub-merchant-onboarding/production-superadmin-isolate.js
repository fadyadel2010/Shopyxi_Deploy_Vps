async page => {
    const context = page.context();
    const evidence = { network: [], console: [], blockedExternalOrigins: [] };
    context._commerceHubProductionEvidence = evidence;
    await context.route('**/*', route => {
        const url = route.request().url();
        if (/^https:\/\/admin\.shopyxi\.com(?:\/|$)/.test(url) || /^(?:about:|data:|blob:)/.test(url)) {
            return route.continue();
        }
        evidence.blockedExternalOrigins.push(url.match(/^https?:\/\/[^/]+/)?.[0] || 'non-http');
        return route.abort('blockedbyclient');
    });
    context.on('response', response => {
        const parsed = response.url().match(/^https:\/\/admin\.shopyxi\.com(\/[^?#]*)/);
        if (!parsed || !parsed[1].startsWith('/api/')) return;
        evidence.network.push({
            method: response.request().method(),
            path: parsed[1],
            status: response.status(),
            correlationId: response.headers()['x-correlation-id'] || null,
        });
    });
    page.on('console', message => {
        if (message.type() === 'error') evidence.console.push({ type: 'error', textOmitted: true });
    });
    return {
        isolatedAtUtc: new Date().toISOString(),
        actualHeadedBrowser: true,
        allowedOrigin: 'https://admin.shopyxi.com',
        externalRequestsAbortedBeforeDispatch: true,
        authenticationStateImported: false,
    };
}
