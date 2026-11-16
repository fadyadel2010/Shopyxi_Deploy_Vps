async page => {
    const evidence = page.context()._commerceHubProductionEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('https://admin.shopyxi.com/super-admin/commercehub/seller-applications');
    await page.getByRole('heading', { name: 'Seller applications', exact: true }).waitFor();
    await page.getByText('No applications match this view.', { exact: true }).waitFor();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/production-superadmin-readonly.png',
        fullPage: true,
        mask: [page.getByText(/^[^\s@]+@[^\s@]+\.[^\s@]+$/).locator('..')],
    });
    const relevant = evidence.network.filter(item => item.path.includes('/commercehub/seller-applications'));
    const writes = relevant.filter(item => item.method !== 'GET');
    return {
        verifiedAtUtc: new Date().toISOString(),
        actualHeadedBrowser: true,
        production: true,
        routeAvailable: true,
        listStatus: relevant.at(-1)?.status || null,
        pendingApplicationsVisible: 0,
        mutationRequestsObserved: writes.length,
        network: relevant,
        consoleErrorCountDuringReadOnlyPass: evidence.console.length,
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
        credentialsOmitted: true,
    };
}
