async page => {
    const checks = await page.evaluate(async () => {
        const result=[];
        for(const path of ['/api/auth/session','/api/affiliate/profile','/api/admin/affiliates/payouts/00000000-0000-0000-0000-000000000001/manual-payout/return']) {
            const r=await fetch(path,{credentials:'include',cache:'no-store'}); let d={}; try{d=await r.json()}catch{}
            result.push({path,status:r.status,code:d.code??null,role:d.user?.role??null,affiliateAccountId:d.affiliateAccountId??null});
        }
        return result;
    });
    return {route:page.url().replace(/^https?:\/\/[^/]+/,'').split('?')[0],title:await page.title(),checks};
}
