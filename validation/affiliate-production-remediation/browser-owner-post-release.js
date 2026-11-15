async page => {
    const session = await page.evaluate(async () => { const r = await fetch('/api/auth/session', { credentials:'include',cache:'no-store' }); const d = await r.json(); return { status:r.status,role:d.user?.role,tenantId:d.user?.tenantId }; });
    if (session.status !== 200 || session.role !== 'Owner' || session.tenantId !== 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0') throw Error('Existing Ahram Owner session required');
    const checks = await page.evaluate(async () => {
        const paths = ['/api/admin/affiliates?page=1&pageSize=20','/api/admin/affiliate/readiness','/api/admin/affiliates/payouts/00000000-0000-0000-0000-000000000001/manual-payout/return'];
        const out=[]; for(const path of paths) { const r=await fetch(path,{credentials:'include',cache:'no-store'}); let code=null; if(r.status!==200) code=(await r.json()).code; out.push({path:path.split('?')[0],status:r.status,code}); } return out;
    });
    if (checks[0].status !== 200 || checks[1].status !== 200 || checks[2].status !== 404) throw Error('Owner post-release financial read regression');
    return {verifiedAtUtc:new Date().toISOString(),session,checks,mutationsSubmitted:false};
}
