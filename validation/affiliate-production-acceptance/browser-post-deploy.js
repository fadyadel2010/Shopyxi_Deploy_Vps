async page => {
    const responses=[], errors=[], controls=[];
    const listener=r=>{if(r.url().includes('/api/affiliate/')) responses.push({method:r.request().method(),path:r.url().replace('https://affiliate.shopyxi.com',''),status:r.status(),correlationId:r.headers()['x-correlation-id']||null});};
    const onError=m=>{if(m.type()==='error') errors.push(m.text());};
    page.on('response',listener);page.on('console',onError);
    const settled=async()=>{
        await page.locator('main .animate-pulse, main .animate-spin').first().waitFor({state:'hidden'});
        await page.waitForFunction(()=>[...document.querySelectorAll('main [style]')].every(n=>!n.style.opacity||Number(n.style.opacity)>=0.99));
    };
    const screenshot=async(name)=>{await settled();await page.screenshot({path:'validation/affiliate-production-acceptance/'+name+'.png',fullPage:true,mask:[page.getByText(/^[^\s@]+@[^\s@]+\.[^\s@]+$/).locator('..')]});};
    try {
        if(!page.url().includes('tab=profile')) {await page.getByRole('button',{name:'Settings',exact:true}).click();await page.getByText('Member Since',{exact:true}).waitFor();}
        const pending=page.waitForResponse(r=>r.url().endsWith('/api/affiliate/profile'));
        await page.reload();
        const response=await pending;
        const data=await response.json();
        await page.getByText('Member Since',{exact:true}).waitFor();
        const expected=await page.evaluate(date=>new Date(date).toLocaleDateString('en-US'),data.createdAtUtc);
        await page.getByText(expected,{exact:true}).waitFor();
        const displayed=await page.getByText('Member Since',{exact:true}).locator('..').innerText();
        await screenshot('profile-date-after-deploy');
        await page.getByRole('button',{name:'Edit payout wallet',exact:true}).click();
        await page.getByRole('button',{name:'Save Wallet',exact:true}).click();
        await page.getByText('Wallet number is required',{exact:true}).waitFor();
        controls.push({control:'Empty wallet save',validation:'Wallet number is required',persistentWriteSubmitted:false});
        await page.getByRole('button',{name:'Cancel',exact:true}).click();
        await page.getByText('No payout wallet configured',{exact:true}).waitFor();
        await page.getByRole('button',{name:'Promote',exact:true}).first().click();
        const search=page.getByPlaceholder('Search products to promote...');
        await search.waitFor();await settled();
        let next=page.waitForResponse(r=>r.url().includes('/api/affiliate/discover/products?')&&r.url().includes('search=QA-AFFILIATE'));
        await search.fill('QA-AFFILIATE');await next;await settled();
        controls.push({control:'Discovery search',content:await page.locator('main').innerText()});
        next=page.waitForResponse(r=>r.url().includes('/api/affiliate/discover/products?')&&r.url().includes('sort=newest'));
        await page.locator('main select').selectOption('newest');await next;await settled();
        controls.push({control:'Discovery newest sort',selected:await page.locator('main select').inputValue()});
        await screenshot('discovery-controls-after-deploy');
        next=page.waitForResponse(r=>r.url().includes('/api/affiliate/discover/products?')&&!r.url().includes('search='));
        await search.fill('');await next;await settled();
        await page.getByRole('button',{name:'Analytics',exact:true}).click();await settled();
        for(const label of ['7 Days','90 Days','30 Days']) {
            next=page.waitForResponse(r=>r.url().includes('/api/affiliate/analytics/summary?'));
            await page.getByRole('button',{name:label,exact:true}).click();await next;
            await page.getByText('No data in this period',{exact:true}).first().waitFor();await settled();
            controls.push({control:'Analytics range '+label,content:await page.locator('main').innerText()});
        }
        await screenshot('analytics-after-deploy');
        await page.getByRole('button',{name:'Settings',exact:true}).click();
        await page.getByText(expected,{exact:true}).waitFor();await settled();
        const writes=responses.filter(r=>r.method!=='GET');
        if(writes.length)throw new Error('Unexpected mutation request; inspect before further testing');
        return {verifiedAtUtc:new Date().toISOString(),profileStatus:response.status(),createdAtUtc:data.createdAtUtc,
            expectedMemberSince:expected,displayedMemberSince:displayed,memberSinceMatchesApi:true,
            accountId:data.affiliateAccountId,walletUnconfigured:!data.payoutWalletNumber,
            controls,responses,errors,mutationRequestsObserved:writes.length};
    } finally {page.off('response',listener);page.off('console',onError);}
}
