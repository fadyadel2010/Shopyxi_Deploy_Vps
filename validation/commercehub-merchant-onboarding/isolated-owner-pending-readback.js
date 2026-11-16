async page => {
    const contextEvidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/commercehub/seller-application');
    await page.getByText('Pending review', { exact: true }).waitFor();
    const status = await page.evaluate(async () => {
        const response = await fetch('/api/admin/commercehub/seller-application', { credentials: 'include' });
        const body = await response.json();
        return {
            httpStatus: response.status,
            state: body.status,
            canSubmit: body.canSubmit,
            canResubmit: body.canResubmit,
            publishingEnabled: body.publishingEnabled,
            historyCount: body.history?.length || 0,
            reference: body.currentApplication?.referenceNumber || null,
        };
    });
    const gate = await page.evaluate(async () => {
        const response = await fetch('/api/admin/commercehub/products', { credentials: 'include' });
        const body = await response.json().catch(() => ({}));
        return { status: response.status, code: body.code || null };
    });
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-owner-pending.png',
        fullPage: true,
    });

    await page.goto('http://admin.localhost:18180/admin/commercehub');
    await page.waitForURL(url => url.pathname === '/');

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        status,
        directCatalogGate: gate,
        directPublishingRouteDenied: true,
        deniedRouteDestination: '/',
        externalRequestsBlocked: contextEvidence.blockedExternalOrigins.length,
        applicationNetwork: contextEvidence.network.filter(item => item.path.includes('/commercehub')),
    };
}
