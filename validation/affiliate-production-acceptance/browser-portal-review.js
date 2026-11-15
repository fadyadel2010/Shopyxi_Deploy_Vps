async page => {
    const root='https://affiliate.shopyxi.com';
    const evidence='validation/affiliate-production-acceptance';
    const observations=[], network=[], errors=[];
    const responseListener=r=>{if(r.url().startsWith(root+'/api/'))network.push({method:r.request().method(),path:r.url().split('?')[0].replace(root,''),status:r.status(),correlationId:r.headers()['x-correlation-id']||null});};
    const consoleListener=m=>{if(['error','warning'].includes(m.type())) errors.push({type:m.type(),message:m.text()});};
    const failureListener=r=>network.push({path:r.url().split('?')[0],failure:r.failure()?.errorText});
    page.on('response',responseListener); page.on('console',consoleListener); page.on('requestfailed',failureListener);
    const capture=async(name,actions)=>{
        await page.waitForFunction(()=>!!document.querySelector('main')?.innerText.trim(),{},{timeout:15000});
        await page.locator('.animate-spin').first().waitFor({state:'hidden',timeout:15000}).catch(()=>{});
        await page.getByText('Loading...', {exact:true}).first().waitFor({state:'hidden',timeout:15000}).catch(()=>{});
        await page.locator('main .animate-pulse').first().waitFor({state:'hidden',timeout:15000});
        await page.waitForFunction(()=>[...document.querySelectorAll('main [style]')].every(n=>!n.style.opacity||Number(n.style.opacity)>=0.99));
        const content=await page.locator('main').innerText();
        observations.push({tab:name,route:page.url().replace(root,''),actions,content,
            buttons:await page.locator('main button').allTextContents(),
            inputs:await page.locator('main input,main select').evaluateAll(ns=>ns.map(n=>({type:n.type,placeholder:n.getAttribute('placeholder'),value:n.type==='email'?'[withheld]':n.value,disabled:n.disabled})))});
        await page.screenshot({path:evidence+'/portal-'+name+'.png',fullPage:true,
            mask:[page.locator('input[type="email"]'),page.getByText(/^[^\s@]+@[^\s@]+\.[^\s@]+$/).locator('..')]});
    };
    try {
        const nav=await page.locator('nav button').allTextContents();
        const tabs=[['Dashboard','dashboard','/dashboard'],['Promote','discover','/discover/products'],['My Links','links','/promotion-links'],
            ['My Landing Pages','landing-pages','/landing-pages'],['Orders','orders','/orders'],['Earnings','earnings','/earnings-summary'],
            ['Withdrawals','payouts','/payouts'],['Analytics','analytics','/analytics/summary'],['Settings','profile','/profile']];
        for(const [label,id,endpoint] of tabs) {
            const pending=page.waitForResponse(r=>r.url().includes('/api/affiliate'+endpoint)&&r.request().method()==='GET');
            if(id==='dashboard' && (!page.url().includes('tab=') || page.url().includes('tab=dashboard'))) await page.reload();
            else await page.getByRole('button',{name:label,exact:true}).first().click();
            const response=await pending;
            await capture(id,['Real sidebar navigation','Backend read '+response.status(),'Loading/empty state inspection']);
        }
        const boundaries=await page.evaluate(async()=>{
            const paths=['/api/affiliate/profile','/api/admin/affiliates','/api/auth/session'];
            const rows=[]; for(const path of paths) {const r=await fetch(path,{credentials:'include',cache:'no-store'}); rows.push({path,status:r.status});} return rows;
        });
        await page.reload();
        await page.getByRole('button',{name:'Settings',exact:true}).first().waitFor();
        await page.getByText('No payout wallet configured',{exact:true}).waitFor();
        await capture('profile-refresh',['Hard refresh retains authenticated identity and selected tab']);
        return {verifiedAtUtc:new Date().toISOString(),identity:'new normal QA Affiliate; existing session only',navigation:nav,observations,network,errors,boundaries};
    } finally {
        page.off('response',responseListener);page.off('console',consoleListener);page.off('requestfailed',failureListener);
    }
}
