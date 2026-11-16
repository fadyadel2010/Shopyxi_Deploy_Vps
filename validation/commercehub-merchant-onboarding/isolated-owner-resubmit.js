async page => {
    const evidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    const reason = 'Please clarify the requested product categories before resubmitting.';
    await page.goto('http://admin.localhost:18180/commercehub/seller-application');
    await page.getByText('Rejected', { exact: true }).first().waitFor();
    await page.getByText(reason, { exact: true }).waitFor();
    await page.getByRole('heading', { name: 'Correct and resubmit your application', exact: true }).waitFor();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-owner-rejected.png',
        fullPage: true,
    });

    await page.getByRole('textbox', { name: 'Short business description', exact: true }).fill(
        'An isolated merchant offering clearly categorized home products, decor, and locally sourced gifts.',
    );
    await page.getByRole('textbox', { name: 'Product categories you plan to offer', exact: true }).fill('Home, Gifts, Decor');
    const pending = page.waitForResponse(response =>
        response.url().endsWith('/api/admin/commercehub/seller-application') &&
        response.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Resubmit for review', exact: true }).click();
    const response = await pending;
    await page.getByText('Pending review', { exact: true }).first().waitFor();
    const pageText = await page.locator('main').innerText();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-owner-resubmitted.png',
        fullPage: true,
    });

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        exactRejectionReasonVisibleBeforeCorrection: true,
        resubmissionStatus: response.status(),
        state: 'PendingReview',
        revisionTwoVisible: pageText.includes('Revision 2'),
        rejectedRevisionRetained: pageText.includes('Revision 1') && pageText.includes(reason),
        publishingStillUnavailable: !pageText.includes('Open product publishing'),
        applicationNetwork: evidence.network.filter(item => item.path.includes('/commercehub/seller-application')),
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
    };
}
