async page => {
    const network=[],errors=[];
    const responseListener=r=>{if(r.url().includes('/api/admin/')||r.url().includes('/api/auth/session'))network.push({path:r.url().split('?')[0].replace('https://admin.shopyxi.com',''),status:r.status(),method:r.request().method(),correlationId:r.headers()['x-correlation-id']||null});};
    const consoleListener=m=>{if(['error','warning'].includes(m.type()))errors.push({type:m.type(),message:m.text()});};
    page.on('response',responseListener);page.on('console',consoleListener);
    try {
        const session=await page.evaluate(async()=>{const r=await fetch('/api/auth/session',{credentials:'include'});const d=await r.json();return {status:r.status,role:d.user?.role,tenantId:d.user?.tenantId};});
        if(session.status!==200||session.role!=='Owner'||session.tenantId!=='cdbe20e0-f84a-4416-b20c-044e6bcf04d0')throw Error('Expected existing Ahram Owner session required');
        if(!page.url().endsWith('/admin/affiliates/products'))throw Error('Existing live Affiliate Products screen required');
        if(await page.getByRole('dialog').count()) await page.keyboard.press('Escape');
        const catalog=await page.locator('main').first().innerText();
        await page.screenshot({path:'validation/affiliate-production-acceptance/merchant-products.png',fullPage:true});
        const row=page.getByRole('row').filter({hasText:'QA-ERP-R10-20261113-TRACKED'});
        const pending=page.waitForResponse(r=>/\/api\/admin\/products\/[^/]+\/affiliate$/.test(r.url().split('?')[0]));
        await row.getByRole('button').click();
        const settingsResponse=await pending;
        const settings=await settingsResponse.json();
        const dialog=page.getByRole('dialog');
        await dialog.waitFor();
        await dialog.locator('.animate-spin').first().waitFor({state:'hidden'}).catch(()=>{});
        const configuration=await dialog.innerText();
        const buttons=await dialog.getByRole('button').allTextContents();
        await page.screenshot({path:'validation/affiliate-production-acceptance/merchant-product-commission-review.png',fullPage:true});
        await page.keyboard.press('Escape');
        await dialog.waitFor({state:'hidden'});
        const pendingList=page.waitForResponse(r=>r.url().includes('/api/admin/affiliates?'));
        await page.locator('a[href="/admin/affiliates"]').click();
        await pendingList;
        await page.getByText('لا توجد أرباح للمسوقين في هذا المتجر بعد',{exact:true}).waitFor();
        const affiliates=await page.locator('main').first().innerText();
        const readiness=await page.evaluate(async()=>{const r=await fetch('/api/admin/affiliate/readiness',{credentials:'include'});return {status:r.status,data:await r.json()};});
        await page.screenshot({path:'validation/affiliate-production-acceptance/merchant-affiliates-empty.png',fullPage:true});
        return {verifiedAtUtc:new Date().toISOString(),session,catalog,settingsStatus:settingsResponse.status(),settings,configuration,buttons,affiliates,readiness,network,errors,
            mutationsSubmitted:false,reason:'No stock, publication or policy writes before a fully reversible synthetic financial/fulfillment journey is established'};
    } finally {page.off('response',responseListener);page.off('console',consoleListener);}
}
