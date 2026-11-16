async page => {
    const contextEvidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/commercehub/seller-application');
    await page.getByRole('heading', { name: 'Become a CommerceHub Seller', exact: true }).waitFor();
    await page.getByText('Not applied', { exact: true }).waitFor();
    const verificationNoticeVisible = await page.getByText(/existing merchant verification must be complete/i).isVisible();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-owner-application-form.png',
        fullPage: true,
    });

    await page.getByLabel('Business category', { exact: true }).fill('Home and gifts');
    await page.getByLabel('Short business description', { exact: true }).fill(
        'An isolated merchant offering practical home products and locally sourced gifts.',
    );
    await page.getByLabel('Product categories you plan to offer', { exact: true }).fill('Home, Gifts');
    await page.getByLabel('Business phone', { exact: true }).fill('+201000000000');

    const pending = page.waitForResponse(response =>
        response.url().endsWith('/api/admin/commercehub/seller-application') &&
        response.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Submit application', exact: true }).click();
    const submission = await pending;
    await page.getByText('Pending review', { exact: true }).waitFor();
    const reference = await page.locator('main').getByText(/^CHS-[0-9]{8}-[A-F0-9]{10}$/).innerText();
    const duplicateSubmitVisible = await page.getByRole('button', { name: 'Submit application', exact: true }).count();

    const gate = await page.evaluate(async () => {
        const response = await fetch('/api/admin/commercehub/products', { credentials: 'include' });
        const body = await response.json().catch(() => ({}));
        return { status: response.status, code: body.code || null };
    });

    await page.goto('http://admin.localhost:18180/admin/commercehub');
    await page.waitForURL(url => url.pathname === '/');
    await page.goto('http://admin.localhost:18180/commercehub/seller-application');
    await page.getByText('Pending review', { exact: true }).waitFor();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-owner-pending.png',
        fullPage: true,
    });

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        submissionStatus: submission.status(),
        reference,
        state: 'PendingReview',
        verificationNoticeVisible,
        duplicateSubmitVisible: duplicateSubmitVisible > 0,
        directCatalogGate: gate,
        directPublishingRouteDenied: true,
        deniedRouteDestination: '/',
        externalRequestsBlocked: contextEvidence.blockedExternalOrigins.length,
        applicationNetwork: contextEvidence.network.filter(item => item.path.includes('/commercehub')),
        consoleErrorCount: contextEvidence.console.length,
    };
}
