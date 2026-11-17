// Headed Chrome CDP. The user authenticates; never reads/injects cookies or tokens.
const fs = require('node:fs')
const phase = process.argv[2] || 'before'
if (!/^[a-z-]+$/.test(phase)) throw Error('Invalid phase')
const expectedBundle = process.argv[3] || null
const delay = ms => new Promise(r => setTimeout(r,ms))
async function main() {
    const pages = await fetch('http://127.0.0.1:9222/json').then(r=>r.json())
    const page = pages.find(p=>p.type==='page' && p.url.startsWith('https://admin.shopyxi.com/'))
    if (!page) throw Error('Headed production browser is not open')
    const ws = new WebSocket(page.webSocketDebuggerUrl)
    await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject})
    let sequence=0
    const pending=new Map(), requests=new Map(), responses=[], failures=[], consoleErrors=[]
    let exceptions=0, businessWrites=0
    const send=(method,params={})=>new Promise((resolve,reject)=>{
        const id=++sequence
        const timer=setTimeout(()=>{pending.delete(id);reject(Error(`CDP timeout ${method}`))},30000)
        pending.set(id,{resolve,reject,timer});ws.send(JSON.stringify({id,method,params}))
    })
    ws.onmessage=event=>{
        const m=JSON.parse(event.data), p=m.params||{}
        if (pending.has(m.id)) { const c=pending.get(m.id);pending.delete(m.id);clearTimeout(c.timer);return m.error?c.reject(Error(m.error.message)):c.resolve(m.result) }
        if(m.method==='Runtime.exceptionThrown') exceptions++
        if(m.method==='Runtime.consoleAPICalled' && p.type==='error') consoleErrors.push({type:'console.error'})
        if(m.method==='Network.requestWillBeSent') {
            const u=new URL(p.request.url)
            requests.set(p.requestId,u.pathname)
            if(u.origin==='https://admin.shopyxi.com' && u.pathname.startsWith('/api/') &&
                !u.pathname.includes('/auth/') && !['GET','HEAD','OPTIONS'].includes(p.request.method)) businessWrites++
        }
        if(m.method==='Network.responseReceived') {
            const u=new URL(p.response.url)
            if(u.pathname.startsWith('/api/super-admin/orders') || u.pathname==='/api/tenants')
                responses.push({path:u.pathname,status:p.response.status})
        }
        if(m.method==='Network.loadingFailed' && (requests.get(p.requestId)||'').startsWith('/api/'))
            failures.push({path:requests.get(p.requestId),cancelled:!!p.canceled})
    }
    const evaluate=async expression=>{
        const r=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true})
        if(r.exceptionDetails) throw Error('Browser expression failed; raw exception not retained')
        return r.result.value
    }
    const wait=async expression=>{
        for(let i=0;i<150;i++){if(await evaluate(expression))return;await delay(200)}
        throw Error('Expected browser state did not complete')
    }
    const navigate=async query=>{
        await send('Page.navigate',{url:'https://admin.shopyxi.com/super-admin/orders'+(query?'?'+query:'')})
        await wait(`location.pathname.includes('login') || !!document.querySelector('input[type=date]')`)
        if((await evaluate('location.pathname')).includes('login')) throw Error('User interactive login required')
        await wait(`!Array.from(document.querySelectorAll('button')).some(b=>['Refreshing','جارٍ التحديث'].includes(b.innerText))`)
    }
    await send('Page.enable');await send('Runtime.enable');await send('Network.enable')
    await send('Network.setCacheDisabled',{cacheDisabled:true});await send('Page.bringToFront')
    await navigate('')
    const baseline=await evaluate(`({
        emptyState: /No orders matched|لا توجد طلبات تطابق/.test(document.querySelector('main')?.innerText||''),
        nativeDateValues:Array.from(document.querySelectorAll('input[type=date]')).map(i=>i.value),
        statusOptions:Array.from(document.querySelectorAll('select')[0]?.options||[]).map(o=>o.value),
        paginationUsesArtificialPage:/Page 1 of 1|صفحة 1 من 1/.test(document.querySelector('main')?.innerText||'')
    })`)
    const captures=[]
    for(const language of ['en','ar']) {
        const direction=language==='ar'?'rtl':'ltr'
        if(await evaluate('document.documentElement.dir')!==direction) {
            await evaluate(`Array.from(document.querySelectorAll('button')).find(b=>['Switch to Arabic','التبديل إلى الإنجليزية'].includes(b.title)).click()`)
            await wait(`document.documentElement.dir==='${direction}'`)
        }
        for(const width of [1440,390]) {
            await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:width<500})
            await delay(500)
            const c=await evaluate(`({
                language:document.documentElement.lang,direction:document.documentElement.dir,
                horizontalOverflow:document.documentElement.scrollWidth>document.documentElement.clientWidth,
                dateInputTypes:Array.from(document.querySelectorAll('input[type=date]')).map(i=>i.type),
                emptyState:/No orders matched|لا توجد طلبات تطابق/.test(document.querySelector('main')?.innerText||'')
            })`)
            const rect=await evaluate(`(()=>{const r=document.querySelector('main').getBoundingClientRect();return {x:Math.max(0,r.x),y:Math.max(0,r.y)+scrollY,width:Math.min(r.width,innerWidth-Math.max(0,r.x)),height:Math.min(4000,r.height)}})()`)
            const image=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{...rect,scale:1}})
            c.width=width;c.screenshot=`orders-${phase}-${language}-${width}.png`
            fs.writeFileSync(`${__dirname}/${c.screenshot}`,Buffer.from(image.data,'base64'));captures.push(c)
        }
    }
    await send('Emulation.clearDeviceMetricsOverride')
    const loadedBundles=await evaluate(`performance.getEntriesByType('resource').map(e=>new URL(e.name).pathname).filter(p=>/^\\/assets\\/index-.*\\.js$/.test(p))`)
    // Only sanitized summaries of authenticated GETs leave the browser.
    const apiChecks=await evaluate(`(async()=>{
        const tests=[
            ['',200],['?search=ORDERS-AUDIT-NONEXISTENT',200],
            ...['Pending','Confirmed','Processing','Shipped','Delivered','Completed','Cancelled','Refunded','OnHold'].map(v=>['?status='+v,200]),
            ...['Paid','Unpaid','Failed'].map(v=>['?paymentStatus='+v,200]),
            ['?fromDate=2026-10-18',200],['?toDate=2026-11-16',200],
            ['?fromDate=2026-10-18&toDate=2026-11-16',200],
            ['?fromDate=2026-11-17&toDate=2026-11-16',400],
            ['?status=not-a-status',400],['?paymentStatus=999',400],
            // Existing global MVC normalization clamps these before controller validation.
            ['?page=0',200],['?pageSize=101',200],
            ...['CreatedAt','OrderNumber','Customer','Tenant','VendorCount','TotalAmount','Status','PaymentStatus']
                .flatMap(v=>['Asc','Desc'].map(d=>['?sortBy='+v+'&sortDirection='+d,200])),
            ['?page=2&pageSize=20',200],
        ];const output=[]
        for(const [query,expected] of tests) {
            const r=await fetch('/api/super-admin/orders'+query)
            const j=await r.json()
            output.push({query,status:r.status,expected,count:j.meta?.totalCount,
                rows:j.data?.orders?.length,totalPages:j.meta?.totalPages,
                correct:r.status===expected && (expected!==200 || (j.meta?.totalCount===0 && j.data?.orders?.length===0))})
        }
        const statsResponse=await fetch('/api/super-admin/orders/stats')
        const stats=(await statsResponse.json()).data
        const csvResponse=await fetch('/api/super-admin/orders/export?search=ORDERS-AUDIT-NONEXISTENT&status=Pending&paymentStatus=Unpaid&fromDate=2026-10-18&toDate=2026-11-16&sortBy=CreatedAt&sortDirection=Desc')
        const bytes=new Uint8Array(await csvResponse.arrayBuffer()), text=new TextDecoder().decode(bytes)
        const tenantsResponse=await fetch('/api/tenants');const tenants=await tenantsResponse.json()
        for(const tenant of Array.isArray(tenants)?tenants:[]) {
            const r=await fetch('/api/super-admin/orders?tenantId='+encodeURIComponent(tenant.id))
            const j=await r.json()
            output.push({query:'store-filter-redacted',status:r.status,expected:200,count:j.meta?.totalCount,rows:j.data?.orders?.length,
                correct:r.status===200 && j.meta?.totalCount===0 && j.data?.orders?.length===0})
        }
        const missing=await fetch('/api/super-admin/orders/11111111-1111-4111-8111-111111111111')
        return {tests:output, stats:{status:statsResponse.status,totalOrdersToday:stats?.totalOrdersToday,pendingOrders:stats?.pendingOrders,
                failedPayments:stats?.failedPayments,capturedSalesToday:stats?.capturedSalesToday,revenueToday:stats?.revenueToday,asOfUtc:stats?.asOfUtc},
            csv:{status:csvResponse.status,headerOnly:text.trim().split(/\\r?\\n/).length===1,
                utf8Bom:bytes[0]===239&&bytes[1]===187&&bytes[2]===191,rowCountHeader:csvResponse.headers.get('X-Export-Row-Count')},
            tenantOptionCount:Array.isArray(tenants)?tenants.length:null,missingDetailStatus:missing.status}
    })()`)
    const checksPassed = apiChecks.tests.every(t=>t.correct) &&
        apiChecks.stats.status===200 && ['totalOrdersToday','pendingOrders','failedPayments','capturedSalesToday'].every(k=>apiChecks.stats[k]===0) &&
        apiChecks.csv.status===200 && apiChecks.csv.headerOnly && apiChecks.csv.utf8Bom && apiChecks.csv.rowCountHeader==='0' &&
        apiChecks.missingDetailStatus===404 && captures.length===4 && captures.every(c=>!c.horizontalOverflow&&c.emptyState&&c.direction===(c.language==='ar'?'rtl':'ltr')) &&
        !!expectedBundle && loadedBundles.some(b=>b.endsWith('/'+expectedBundle)) &&
        !baseline.paginationUsesArtificialPage && !consoleErrors.length && !exceptions && !businessWrites && !failures.length
    const result={capturedAtUtc:new Date().toISOString(),actualHeadedBrowser:true,credentialsReadOrInjected:false,
        sourcePhase:phase,expectedBundle,loadedBundles,baseline,captures,apiChecks,
        consoleErrorCount:consoleErrors.length,exceptionCount:exceptions,businessWriteCount:businessWrites,networkFailures:failures,
        checksPassed,productionScope:'EMPTY_COMMERCEHUB_ONLY',populatedLifecyclesCertifiedByProduction:false,
        status:phase==='after'&&checksPassed?'PASS_BROWSER_EMPTY_PRODUCTION_SCOPE':'OBSERVED_NOT_RELEASE_ACCEPTANCE'}
    fs.writeFileSync(`${__dirname}/browser-${phase}.json`,JSON.stringify(result,null,2)+'\n')
    console.log(JSON.stringify(result,null,2));ws.close()
    if (phase==='after' && !checksPassed) process.exitCode=1
}
main().catch(e=>{console.error(e.message);process.exitCode=1})
