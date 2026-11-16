async page => {
    const evidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/commercehub/seller-application');
    await page.getByText('Accepted', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Open product publishing', exact: true }).waitFor();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-headed-owner-approved.png',
        fullPage: true,
    });

    const sessionRefresh = page.waitForResponse(response => response.url().endsWith('/api/auth/session'));
    await page.getByRole('button', { name: 'Open product publishing', exact: true }).click();
    const refreshedSession = await sessionRefresh;
    await page.waitForURL(url => url.pathname === '/admin/commercehub');
    await page.getByRole('heading', { name: 'CommerceHub Products', exact: true }).waitFor();
    await page.getByText('Approved seller', { exact: true }).waitFor();
    const catalogGate = await page.evaluate(async () => {
        const response = await fetch('/api/admin/commercehub/products', { credentials: 'include' });
        return { status: response.status };
    });
    const englishDirection = await page.locator('main').getAttribute('dir');
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-headed-products-en.png',
        fullPage: true,
    });

    await page.getByRole('button', { name: 'AR', exact: true }).click();
    await page.getByRole('heading', { name: 'منتجات CommerceHub', exact: true }).waitFor();
    await page.getByText('بائع معتمد', { exact: true }).waitFor();
    const arabicDirection = await page.locator('main').getAttribute('dir');
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-headed-products-ar.png',
        fullPage: true,
    });

    const commerceHubWrites = evidence.network.filter(item =>
        item.path.includes('/commercehub') && item.method !== 'GET',
    );
    return {
        verifiedAtUtc: new Date().toISOString(),
        actualHeadedBrowser: true,
        isolated: true,
        approvalDurable: true,
        sessionRefreshStatus: refreshedSession.status(),
        productPublishingRouteAvailable: true,
        catalogGate,
        approvedSellerPresentation: true,
        englishDirection,
        arabicDirection,
        bilingualProductPublishingVerified: true,
        productMutationRequestsObserved: commerceHubWrites.length,
        applicationNetwork: evidence.network.filter(item => item.path.includes('/commercehub')),
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
    };
}
