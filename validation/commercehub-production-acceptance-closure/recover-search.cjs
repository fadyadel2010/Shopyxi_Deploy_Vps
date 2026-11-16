// Explicitly approved derived-data recovery, separate from read-only UI acceptance.
// One attempt only. Never publish/update a listing, touch source data, or log credentials.
const fs = require('node:fs')
const path = require('node:path')
const { connect } = require('./production-cdp.cjs')
const output = path.join(__dirname, 'search-recovery.json')
async function main() {
    if (fs.existsSync(output)) throw new Error('Recovery receipt already exists: inspect before any further action')
    const c = await connect()
    try {
        const before = await c.evaluate(`(async()=>{
            const get=async p=>{const r=await fetch(p,{credentials:'include',cache:'no-store'});if(!r.ok)throw new Error('Read failed '+r.status);return r.json()};
            const s=await get('/api/auth/session');
            const products=await get('/api/admin/commercehub/products');
            const product=await get('/api/products/b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8');
            const variant=products.find(p=>p.productId==='b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8')?.variants.find(v=>v.variantId==='81069782-7bda-4e35-bf24-3e13424f06f2');
            const keys=['salePrice','purchasePrice','units','variants','warehouseStocks','isOnlineVisible','publishStatus'];
            return {session:{role:s.user?.role,tenantId:s.user?.tenantId},product:JSON.stringify(Object.fromEntries(keys.map(k=>[k,product[k]]))),
                listing:variant?.listing,status:await get('/api/admin/commercehub/search/index-status')};
        })()`)
        if (before.session.role !== 'Owner' || before.session.tenantId !== 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0' ||
            before.listing?.listingId !== 'ff8cdce3-ad88-4467-b027-cdde5812e84d' || before.listing.commerceHubPrice !== 12)
            throw new Error('Recovery authority or baseline mismatch')
        const receipt = {
            approved: true, approval: 'User explicitly selected Repair derived search',
            startedAtUtc: new Date().toISOString(), method: 'POST', path: '/api/admin/commercehub/search/reindex',
            session: before.session, beforeIndexStatus: before.status, listingBefore: before.listing,
            attemptCount: 1, outcome: 'STARTED',
        }
        fs.writeFileSync(output, JSON.stringify(receipt, null, 2) + '\n')
        const response = await c.evaluate(`(async()=>{
            // Same genuine cookie/CSRF flow as the existing application's API client.
            const cookie=document.cookie.split(';').map(s=>s.trim()).find(s=>s.startsWith('csrf_token='));
            if(!cookie)throw new Error('Existing CSRF cookie unavailable; user session required');
            const response=await fetch('/api/admin/commercehub/search/reindex',{method:'POST',credentials:'include',
                headers:{'X-CSRF-Token':decodeURIComponent(cookie.substring('csrf_token='.length)),'Accept-Language':document.documentElement.lang}});
            const body=await response.json();
            return {status:response.status,body};
        })()`)
        receipt.response = response
        receipt.completedAtUtc = new Date().toISOString()
        receipt.outcome = response.status === 200 && response.body.failed === 0 ? 'COMPLETED' : 'FAILED_REVIEW_REQUIRED'
        const after = await c.evaluate(`(async()=>{
            const get=async p=>{const r=await fetch(p,{credentials:'include',cache:'no-store'});if(!r.ok)throw new Error('Read failed '+r.status);return r.json()};
            const products=await get('/api/admin/commercehub/products');
            const product=await get('/api/products/b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8');
            const variant=products.find(p=>p.productId==='b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8')?.variants.find(v=>v.variantId==='81069782-7bda-4e35-bf24-3e13424f06f2');
            const keys=['salePrice','purchasePrice','units','variants','warehouseStocks','isOnlineVisible','publishStatus'];
            return {product:JSON.stringify(Object.fromEntries(keys.map(k=>[k,product[k]]))),listing:variant?.listing,
                status:await get('/api/admin/commercehub/search/index-status')};
        })()`)
        receipt.afterIndexStatus = after.status
        receipt.protectedProductUnchanged = before.product === after.product
        receipt.listingUnchanged = JSON.stringify(before.listing) === JSON.stringify(after.listing)
        fs.writeFileSync(output, JSON.stringify(receipt, null, 2) + '\n')
        console.log(JSON.stringify(receipt, null, 2))
        if (receipt.outcome !== 'COMPLETED' || !receipt.protectedProductUnchanged || !receipt.listingUnchanged)
            throw new Error('Recovery did not pass; no automatic repeat')
    } finally { c.close() }
}
main().catch(error => { console.error(error.message); process.exitCode = 1 })
