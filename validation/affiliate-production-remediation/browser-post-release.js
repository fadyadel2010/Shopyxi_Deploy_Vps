async page => {
    const root = 'https://affiliate.shopyxi.com', network = [], errors = [];
    const onResponse = r => { if (r.url().startsWith(root + '/api/')) network.push({ method: r.request().method(), path: r.url().split('?')[0].replace(root,''), status: r.status() }); };
    const onError = m => { if (m.type() === 'error') errors.push(m.text()); };
    page.on('response', onResponse); page.on('console', onError);
    try {
        await page.goto(root + '/affiliate/portal?tab=profile');
        const profile = await page.evaluate(async () => { const r = await fetch('/api/affiliate/profile', { credentials: 'include', cache: 'no-store' }); const p = await r.json(); return { status: r.status, accountId: p.affiliateAccountId, createdAtUtc: p.createdAtUtc }; });
        if (profile.status !== 200 || profile.accountId !== '01a1dc7c-3cb5-7b17-a0f2-1d082c878a60') throw Error('Existing normal QA Affiliate session required');
        const tabs = [['Dashboard','/dashboard'],['Promote','/discover/products'],['My Links','/promotion-links'],['My Landing Pages','/landing-pages'],['Orders','/orders'],['Earnings','/earnings-summary'],['Withdrawals','/payouts'],['Analytics','/analytics/summary'],['Settings','/profile']];
        const observations = [];
        for (const [label, endpoint] of tabs) {
            const pending = page.waitForResponse(r => r.url().includes('/api/affiliate' + endpoint) && r.request().method() === 'GET');
            await page.getByRole('button', { name: label, exact: true }).first().click();
            const response = await pending;
            await page.locator('main .animate-pulse, main .animate-spin').first().waitFor({ state: 'hidden' });
            observations.push({ tab: label, status: response.status(), mainPresent: !!(await page.locator('main').innerText()).trim() });
        }
        const boundaries = await page.evaluate(async () => {
            const paths = ['/api/admin/affiliates/payouts/00000000-0000-0000-0000-000000000001/manual-payout/return'];
            const out = []; for (const path of paths) { const r = await fetch(path, { credentials: 'include', cache: 'no-store' }); let d={}; try{d=await r.json()}catch{} out.push({ path, status: r.status, code:d.code??null }); } return out;
        });
        const expected = await page.evaluate(d => new Date(d).toLocaleDateString('en-US'), profile.createdAtUtc);
        await page.getByText(expected, { exact: true }).waitFor();
        const writes = network.filter(r => r.method !== 'GET');
        const passed = writes.length===0 && observations.every(o => o.status===200) && boundaries.every(b => b.status===403 || (b.status===400 && b.code==='TENANT_REQUIRED'));
        return { verifiedAtUtc: new Date().toISOString(), passed, profile, memberSinceMatchesApi: true, observations, boundaries, network, errors, mutationRequestsObserved: writes.length };
    } finally { page.off('response', onResponse); page.off('console', onError); }
}
