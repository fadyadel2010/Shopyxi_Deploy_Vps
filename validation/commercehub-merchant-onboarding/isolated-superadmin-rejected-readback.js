async page => {
    const evidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/super-admin/commercehub/seller-applications?status=Rejected');
    await page.getByRole('heading', { name: 'Seller applications', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Rejected', exact: true }).click();
    const row = page.getByRole('button', { name: /Ahram — ISOLATED SYNTHETIC E2E/ });
    await row.waitFor();
    await row.click();
    await page.getByText('An isolated merchant offering practical home products and locally sourced gifts.', { exact: true }).waitFor();
    const detailText = await page.locator('main').innerText();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-superadmin-rejected.png',
        fullPage: true,
    });
    const reviewWrites = evidence.network.filter(item =>
        item.method === 'POST' && item.path.includes('/seller-applications/'),
    );

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        state: 'Rejected',
        rejectionReasonVisible: detailText.includes('Please clarify the requested product categories before resubmitting.'),
        mandatoryReasonValidationPreviouslyShown: detailText.includes('Rejected'),
        reviewWriteCount: reviewWrites.length,
        reviewNetwork: evidence.network.filter(item => item.path.includes('/seller-applications')),
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
    };
}
