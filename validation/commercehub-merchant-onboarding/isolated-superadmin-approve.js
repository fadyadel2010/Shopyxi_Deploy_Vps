async page => {
    const evidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/super-admin/commercehub/seller-applications');
    await page.getByRole('heading', { name: 'Seller applications', exact: true }).waitFor();
    const row = page.getByRole('button', { name: /Ahram — ISOLATED SYNTHETIC E2E/ });
    await row.waitFor();
    await row.click();
    await page.getByText('An isolated merchant offering clearly categorized home products, decor, and locally sourced gifts.', { exact: true }).waitFor();
    await page.getByText('Complete', { exact: true }).waitFor();
    const approveButton = page.getByRole('button', { name: 'Approve seller', exact: true });
    const approvalEnabled = await approveButton.isEnabled();
    const pending = page.waitForResponse(response =>
        /\/api\/super-admin\/commercehub\/seller-applications\/[0-9a-f-]+\/approve$/.test(response.url()) &&
        response.request().method() === 'POST',
    );
    await approveButton.click();
    const response = await pending;
    await page.getByRole('status').waitFor();
    await page.getByText('Approved', { exact: true }).last().waitFor();
    const detailText = await page.locator('main').innerText();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-superadmin-approved.png',
        fullPage: true,
    });

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        existingVerificationComplete: true,
        approvalEnabled,
        approvalStatus: response.status(),
        decisionState: 'Approved',
        marketplaceApprovalActive: detailText.includes('Active'),
        revisionHistoryRetained: detailText.includes('Revision 1') && detailText.includes('Revision 2'),
        reviewNetwork: evidence.network.filter(item => item.path.includes('/seller-applications')),
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
    };
}
