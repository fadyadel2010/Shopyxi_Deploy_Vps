async page => {
    // Run only in the dedicated anonymous headed Affiliate browser. No enrollment or login submission.
    const root = 'https://affiliate.shopyxi.com';
    const evidence = 'validation/affiliate-production-acceptance';
    const observations = [];
    const network = [];
    const consoleMessages = [];
    const deniedMutations = [];
    const cleanUrl = value => value.split('?')[0];
    const responseListener = response => {
        if (!response.url().startsWith(root + '/api/')) return;
        network.push({method: response.request().method(), url: cleanUrl(response.url()), status: response.status(),
            correlationId: response.headers()['x-correlation-id'] || null});
    };
    const consoleListener = message => {
        if (['error', 'warning'].includes(message.type())) consoleMessages.push({type: message.type(), text: message.text()});
    };
    const failureListener = request => network.push({method: request.method(), url: cleanUrl(request.url()), failure: request.failure()?.errorText});
    const guard = async route => {
        const request = route.request();
        if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method()) && !request.url().endsWith('/auth/refresh')) {
            deniedMutations.push({method: request.method(), url: cleanUrl(request.url())});
            return route.abort('blockedbyclient');
        }
        return route.continue();
    };
    page.on('response', responseListener);
    page.on('console', consoleListener);
    page.on('requestfailed', failureListener);
    await page.route(root + '/api/**', guard);
    const capture = async (name, actions) => {
        const text = await page.locator('body').innerText();
        observations.push({name, route: page.url().replace(root,''),
            title: await page.title(), actions, text,
            links: await page.locator('a[href]').evaluateAll(nodes => nodes.map(n => ({label: n.innerText, href: n.getAttribute('href')})))});
        await page.screenshot({path: evidence + '/' + name + '.png', fullPage: true});
    };
    try {
        await page.reload();
        await page.getByRole('heading', {name: 'Join as Affiliate', exact: true}).waitFor();
        await page.getByRole('button', {name: 'Create Affiliate Account', exact: true}).click();
        const emptyInvalid = await page.locator('input:invalid').count();
        await page.getByRole('textbox', {name: 'Display Name', exact: true}).fill('QA');
        await page.getByRole('textbox', {name: 'Email', exact: true}).fill('not-an-email');
        const emailInvalid = await page.locator('#aff-reg-email').evaluate(node => !node.validity.valid);
        await page.getByRole('textbox', {name: 'Email', exact: true}).fill('validation-only@example.invalid');
        await page.locator('#aff-reg-password').fill('weakpass');
        await page.locator('#aff-reg-confirm').fill('weakpass');
        await page.getByRole('button', {name: 'Create Affiliate Account', exact: true}).click();
        const weakRejected = await page.locator('body').innerText();
        await page.locator('#aff-reg-password').fill('');
        await page.locator('#aff-reg-confirm').fill('');
        await page.getByRole('textbox', {name: 'Email', exact: true}).fill('');
        await page.getByRole('textbox', {name: 'Display Name', exact: true}).fill('');
        await capture('registration-validation', {emptyInvalid, emailInvalid, weakPasswordErrorVisible: /uppercase|special|strong/i.test(weakRejected), registrationSubmitted: false});
        await page.getByRole('link', {name: 'Sign in', exact: true}).click();
        await page.getByRole('heading', {level: 1}).waitFor();
        await capture('login', ['UI navigation from registration; no credentials entered']);
        const forgot = page.locator('a[href="/affiliate/forgot-password"]');
        await forgot.click();
        await page.getByRole('heading', {level: 1}).waitFor();
        await capture('forgot-password', ['UI navigation; no email submitted or reset issued']);
        // Token-consuming routes are source-discovered, not live-menu discoveries.
        await page.goto(root + '/affiliate/verify-email');
        await page.getByRole('link', {name: /sign in/i}).waitFor();
        await capture('verify-email-no-token', ['Missing-token safe error; no token supplied']);
        await page.goto(root + '/affiliate/reset-password');
        await page.getByRole('heading', {level: 1}).waitFor();
        await capture('reset-password-no-token', ['No token supplied; no password submitted']);
        await page.goto(root + '/affiliate');
        await page.getByRole('heading', {name: 'Featured Products', exact: true}).waitFor();
        await page.getByRole('status', {name: 'Loading...', exact: true}).first().waitFor({state: 'hidden'}).catch(() => {});
        await capture('landing', ['Landing, categories and product results after loading']);
        const categories = await page.locator('a[href^="/affiliate/category/"]').evaluateAll(nodes => nodes.map(n => n.getAttribute('href')));
        if (categories.length) {
            await page.locator('a[href="' + categories[0] + '"]').first().click();
            await page.getByRole('heading', {level: 1}).waitFor();
            await capture('category', ['First actual category link from landing; no promotion created']);
            await page.goBack();
        }
        const search = page.getByRole('textbox', {name: 'Search products...', exact: true});
        await search.fill('QA-AFFILIATE-NO-MATCH-20261115');
        await page.getByText(/no products/i).first().waitFor();
        await capture('landing-search-empty', ['Safe search empty state']);
        await search.fill('');
        await page.reload();
        await page.getByRole('heading', {name: 'Featured Products', exact: true}).waitFor();
        await page.goto(root + '/affiliate/portal');
        await page.waitForURL('**/affiliate/login?returnUrl=*');
        await capture('portal-anonymous-guard', ['Anonymous portal redirects to login with preserved returnUrl; no auth state copied']);
        return {verifiedAtUtc: new Date().toISOString(), identity: 'anonymous dedicated headed msedge', observations, network, consoleMessages, deniedMutations};
    } finally {
        page.off('response', responseListener);
        page.off('console', consoleListener);
        page.off('requestfailed', failureListener);
        await page.unroute(root + '/api/**', guard);
    }
}
