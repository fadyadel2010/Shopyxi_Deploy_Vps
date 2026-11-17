// Real production UI actions only. No response mocking, token reads or business writes.
const fs = require('node:fs')
const path = require('node:path')
const { connect } = require('./headed-cdp.cjs')
async function main() {
    const c = await connect()
    const requests=[], responses=[], failures=[], requestPaths=new Map()
    let exceptions=0, consoleErrors=0, businessWrites=0
    let stage='initial-navigation'
    c.listeners.push((method,p)=>{
        if(method==='Runtime.exceptionThrown')exceptions++
        if(method==='Runtime.consoleAPICalled'&&p.type==='error')consoleErrors++
        if(method==='Network.requestWillBeSent'){
            const u=new URL(p.request.url)
            requestPaths.set(p.requestId,{origin:u.origin,
                path:u.origin==='https://admin.shopyxi.com'?u.pathname:undefined,resourceType:p.type})
            if(u.pathname.startsWith('/api/super-admin/orders'))
                requests.push({path:u.pathname,at:Date.now()})
            if(u.origin==='https://admin.shopyxi.com'&&u.pathname.startsWith('/api/')&&
                !u.pathname.includes('/auth/')&&!['GET','HEAD','OPTIONS'].includes(p.request.method)) businessWrites++
        }
        if(method==='Network.responseReceived' && p.response.url.includes('/orders/export'))
            responses.push({path:new URL(p.response.url).pathname,status:p.response.status,mimeType:p.response.mimeType})
        if(method==='Network.loadingFailed'&&!p.canceled)failures.push({
            type:'network-failure',stage,...requestPaths.get(p.requestId),error:p.errorText,blockedReason:p.blockedReason})
    })
    const ready=()=>c.wait(`!Array.from(document.querySelectorAll('button')).some(b=>['Refreshing','جارٍ التحديث'].includes(b.innerText))`)
    const click=async labels=>{
        await c.evaluate(`Array.from(document.querySelectorAll('button')).find(b=>${JSON.stringify(labels)}.includes(b.innerText))?.click()`)
        await c.delay(250);await ready()
    }
    const set=async(selector,value)=>{
        await c.evaluate(`(()=>{
            const node=document.querySelector(${JSON.stringify(selector)})
            if(!node)throw Error('Control missing')
            const proto=node.tagName==='SELECT'?HTMLSelectElement.prototype:HTMLInputElement.prototype
            Object.getOwnPropertyDescriptor(proto,'value').set.call(node,${JSON.stringify(value)})
            node.dispatchEvent(new Event('input',{bubbles:true}));node.dispatchEvent(new Event('change',{bubbles:true}))
        })()`)
        await c.delay(650);await ready()
    }
    await c.send('Network.setCacheDisabled',{cacheDisabled:true})
    await c.send('Page.navigate',{url:'https://admin.shopyxi.com/super-admin/orders'})
    await c.wait(`!!document.querySelector('input[type=date]')`);await ready()
    await c.wait(`document.visibilityState==='visible'&&navigator.onLine`)
    // The empty production scope was independently confirmed by SQL.
    stage='filters'
    const checks=[]
    await set('input[aria-label="Search orders"],input[aria-label="بحث الطلبات"]','ORDERS-ACCEPTANCE-NONEXISTENT')
    checks.push({name:'search-url',pass:await c.evaluate(`new URLSearchParams(location.search).get('search')==='ORDERS-ACCEPTANCE-NONEXISTENT'`)})
    for(const [index,parameter] of [[0,'status'],[1,'paymentStatus'],[2,'tenantId']]){
        const values=await c.evaluate(`Array.from(document.querySelectorAll('select')[${index}].options).map(o=>o.value).filter(Boolean)`)
        for(const value of values){
            await set(`select[aria-label="${index===0?'Order status':index===1?'Payment status':'Order tenant'}"],select[aria-label="${index===0?'حالة الطلب':index===1?'حالة الدفع':'متجر الطلب'}"]`,value)
            const pass=await c.evaluate(`new URLSearchParams(location.search).get('${parameter}')===${JSON.stringify(value)}`)
            checks.push({name:parameter==='tenantId'?'store-filter-redacted':parameter+'-'+value,pass})
        }
        await set(`select[aria-label="${index===0?'Order status':index===1?'Payment status':'Order tenant'}"],select[aria-label="${index===0?'حالة الطلب':index===1?'حالة الدفع':'متجر الطلب'}"]`,'')
    }
    await set('input[type=date]','2026-10-18')
    checks.push({name:'from-only',pass:await c.evaluate(`new URLSearchParams(location.search).get('fromDate')==='2026-10-18'`)})
    await set('input[type=date]','')
    await set('input[aria-label="Orders created through date"],input[aria-label="طلبات أُنشئت حتى تاريخ"]','2026-11-16')
    checks.push({name:'to-only',pass:await c.evaluate(`new URLSearchParams(location.search).get('toDate')==='2026-11-16'`)})
    await set('input[type=date]','2026-10-18')
    checks.push({name:'combined-dates',pass:await c.evaluate(`new URLSearchParams(location.search).get('fromDate')==='2026-10-18'&&new URLSearchParams(location.search).get('toDate')==='2026-11-16'`)})
    stage='reload'
    await c.send('Page.reload',{ignoreCache:true});await c.wait(`!!document.querySelector('input[type=date]')`);await ready()
    stage='sorting-and-reset'
    checks.push({name:'reload-shareable',pass:await c.evaluate(`document.querySelector('input[type=date]').value==='2026-10-18'&&new URLSearchParams(location.search).get('search')==='ORDERS-ACCEPTANCE-NONEXISTENT'`)})
    for(let i=0;i<8;i++){
        await c.evaluate(`document.querySelectorAll('thead button')[${i}].click()`)
        await c.delay(300);await ready()
        checks.push({name:'sort-column-'+i,pass:await c.evaluate(`!!document.querySelector('th[aria-sort="ascending"],th[aria-sort="descending"]')`)})
    }
    for(const size of ['50','100','20']){
        await set('select[aria-label="Orders per page"],select[aria-label="الطلبات لكل صفحة"]',size)
        checks.push({name:'page-size-'+size,pass:await c.evaluate(`new URLSearchParams(location.search).get('pageSize')===${JSON.stringify(size)}`)})
    }
    await set('input[type=date]','2026-11-20')
    checks.push({name:'reversed-date-blocked',pass:await c.evaluate(`/Invalid order filters|مرشحات الطلبات غير صالحة/.test(document.querySelector('main').innerText)`)})
    await click(['Reset filters','إعادة ضبط المرشحات'])
    checks.push({name:'reset-all',pass:await c.evaluate(`(()=>{
        const p=new URLSearchParams(location.search)
        return !['search','status','paymentStatus','tenantId','fromDate','toDate'].some(k=>p.has(k))&&p.get('sortBy')==='CreatedAt'&&p.get('sortDirection')==='Desc'&&p.get('page')==='1'
    })()`)})
    await click(['Refresh','تحديث'])
    checks.push({name:'refresh-preserves-default-url',pass:await c.evaluate(`new URLSearchParams(location.search).get('page')==='1'`)})
    const downloadRoot=path.join(process.env.LOCALAPPDATA,'Temp','opencode','orders-browser-downloads')
    fs.mkdirSync(downloadRoot,{recursive:true})
    // A fresh owned run folder prevents an earlier acceptance download from
    // being overwritten and then mistaken for "no newly downloaded file".
    const folder=fs.mkdtempSync(path.join(downloadRoot,'acceptance-'))
    const previousDownloads = new Set(fs.readdirSync(folder))
    await c.send('Page.setDownloadBehavior',{behavior:'allow',downloadPath:folder})
    stage='export'
    await click(['Export CSV','تصدير CSV'])
    await c.wait(`/CSV downloaded|تم تنزيل CSV/.test(document.querySelector('main').innerText)`)
    checks.push({name:'real-ui-export-api-and-mime',pass:responses.some(r=>r.path==='/api/super-admin/orders/export'&&r.status===200&&r.mimeType.includes('csv'))})
    await c.delay(1000)
    const downloaded = fs.readdirSync(folder).filter(name=>!previousDownloads.has(name)&&name.endsWith('.csv'))
    const csv = downloaded.length===1 ? fs.readFileSync(path.join(folder,downloaded[0])) : null
    checks.push({name:'real-downloaded-file-empty-scope-bom-and-header',pass:!!csv &&
        csv[0]===239 && csv[1]===187 && csv[2]===191 &&
        csv.toString('utf8').trim().split(/\r?\n/).length===1 &&
        csv.toString('utf8').includes('Order ID,Order Number')})
    const pollingStarted=Date.now(), startCount=requests.length
    stage='polling'
    const pollingStateBefore=await c.evaluate(`({visibility:document.visibilityState,online:navigator.onLine})`)
    await c.delay(32_000);await ready()
    const polled=requests.slice(startCount)
    const pollingStateAfter=await c.evaluate(`({visibility:document.visibilityState,online:navigator.onLine})`)
    checks.push({name:'polling-observed-in-visible-online-tab',
        pass:pollingStateBefore.visibility==='visible'&&pollingStateAfter.visibility==='visible'&&pollingStateBefore.online&&pollingStateAfter.online})
    checks.push({name:'both-orders-and-stats-poll',pass:polled.some(r=>r.path==='/api/super-admin/orders')&&polled.some(r=>r.path==='/api/super-admin/orders/stats')})
    checks.push({name:'truthful-active-indicator',pass:await c.evaluate(`/Auto-refresh active|التحديث التلقائي نشط/.test(document.querySelector('main').innerText)`)})
    stage='leaving-page'
    await c.send('Page.navigate',{url:'about:blank'});await c.delay(1000)
    const leftCount=requests.length
    await c.delay(32_000)
    checks.push({name:'poll-cleanup-on-leaving',pass:requests.length===leftCount})
    stage='returning-page'
    await c.send('Page.navigate',{url:'https://admin.shopyxi.com/super-admin/orders'})
    await c.wait(`!!document.querySelector('input[type=date]')`);await ready()
    const policyBlockedResources=failures.filter(f=>f.origin==='https://static.cloudflareinsights.com'&&
        f.resourceType==='Script'&&f.blockedReason==='csp')
    const networkFailures=failures.filter(f=>!policyBlockedResources.includes(f))
    const result={capturedAtUtc:new Date().toISOString(),actualHeadedBrowser:true,credentialsReadOrInjected:false,
        businessWriteCount:businessWrites,consoleErrorCount:consoleErrors,exceptionCount:exceptions,networkFailures,
        policyBlockedResources,allFailureEvents:failures,
        downloadEvidence:{newCsvFiles:downloaded.length,utf8Bom:!!csv&&csv[0]===239&&csv[1]===187&&csv[2]===191,
            headerOnly:!!csv&&csv.toString('utf8').trim().split(/\r?\n/).length===1},
        checks,exportResponses:responses,pollingObservedMs:Date.now()-pollingStarted,
        pollingStateBefore,pollingStateAfter,pollingRequests:polled,
        pass:checks.every(x=>x.pass)&&!businessWrites&&!consoleErrors&&!exceptions&&!networkFailures.length}
    fs.writeFileSync(`${__dirname}/browser-actions-after.json`,JSON.stringify(result,null,2)+'\n')
    console.log(JSON.stringify(result,null,2));c.close()
    if(!result.pass)process.exitCode=1
}
main().catch(e=>{console.error(e.message);process.exitCode=1})
