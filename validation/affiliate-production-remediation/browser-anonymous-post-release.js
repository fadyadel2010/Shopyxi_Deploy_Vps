async page => {
    await page.goto('https://affiliate.shopyxi.com/affiliate/login');
    const result = await page.evaluate(async () => {
        const paths=['/api/affiliate/profile','/api/admin/affiliates/payouts/00000000-0000-0000-0000-000000000001/manual-payout/return'];
        const checks=[]; for(const path of paths) { const r=await fetch(path,{credentials:'include',cache:'no-store'}); let d={}; try{d=await r.json()}catch{} checks.push({path,status:r.status,code:d.code??null}); } return checks;
    });
    if(result[0].status!==401 || !(result[1].status===401 || (result[1].status===400 && result[1].code==='TENANT_REQUIRED'))) throw Error('Anonymous financial read must remain unauthorized or rejected for missing store authority');
    return {verifiedAtUtc:new Date().toISOString(),checks:result,mutationsSubmitted:false};
}
