async page => {
    const evidence = page.context()._affiliateFullEvidence || { network: [], console: [], blockedExternalOrigins: [] };
    await page.goto('http://admin.localhost:18180/super-admin/commercehub/seller-applications');
    await page.getByRole('heading', { name: 'Seller applications', exact: true }).waitFor();
    const row = page.getByRole('button', { name: /Ahram — ISOLATED SYNTHETIC E2E/ });
    await row.waitFor();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-superadmin-inbox.png',
        fullPage: true,
    });
    await row.click();
    await page.getByText('An isolated merchant offering practical home products and locally sourced gifts.', { exact: true }).waitFor();

    const writesBeforeValidation = evidence.network.filter(item =>
        item.method === 'POST' && item.path.includes('/seller-applications/'),
    ).length;
    await page.getByRole('button', { name: 'Reject', exact: true }).click();
    const validationMessage = page.getByRole('alert');
    await validationMessage.waitFor();
    const validationText = await validationMessage.innerText();
    const writesAfterValidation = evidence.network.filter(item =>
        item.method === 'POST' && item.path.includes('/seller-applications/'),
    ).length;

    const reason = 'Please clarify the requested product categories before resubmitting.';
    await page.getByLabel('Rejection reason', { exact: true }).fill(reason);
    const pending = page.waitForResponse(response =>
        /\/api\/super-admin\/commercehub\/seller-applications\/[0-9a-f-]+\/reject$/.test(response.url()) &&
        response.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Reject', exact: true }).click();
    const response = await pending;
    await page.getByRole('status').waitFor();
    await page.getByText('Rejected', { exact: true }).last().waitFor();
    const detailText = await page.locator('main').innerText();
    await page.screenshot({
        path: 'validation/commercehub-merchant-onboarding/isolated-superadmin-rejected.png',
        fullPage: true,
    });

    return {
        verifiedAtUtc: new Date().toISOString(),
        headed: true,
        isolated: true,
        mandatoryReasonValidationShown: validationText.includes('at least 10 characters'),
        validationSubmittedMutation: writesAfterValidation > writesBeforeValidation,
        rejectionStatus: response.status(),
        rejectionReason: reason,
        rejectionReasonVisible: detailText.includes(reason),
        decisionState: 'Rejected',
        externalRequestsBlocked: evidence.blockedExternalOrigins.length,
        reviewNetwork: evidence.network.filter(item => item.path.includes('/seller-applications')),
    };
}
