// Observe real headed-page polling without credentials, response bodies or query strings.
const fs=require('node:fs')
const {connect}=require('./headed-cdp.cjs')
async function main(){
    const c=await connect()
    try{
        const requests=[],responses=[],failures=[],paths=new Map()
        c.listeners.push((method,p)=>{
            if(method==='Network.requestWillBeSent'){
                const u=new URL(p.request.url)
                const row={origin:u.origin,path:u.origin==='https://admin.shopyxi.com'?u.pathname:undefined,resourceType:p.type}
                paths.set(p.requestId,row)
                if(u.pathname==='/api/super-admin/orders'||u.pathname==='/api/super-admin/orders/stats')
                    requests.push({...row,at:Date.now()})
            }
            if(method==='Network.responseReceived'&&p.response.url.startsWith('https://admin.shopyxi.com/api/super-admin/orders'))
                responses.push({path:new URL(p.response.url).pathname,status:p.response.status,at:Date.now()})
            if(method==='Network.loadingFailed'&&!p.canceled)
                failures.push({...paths.get(p.requestId),error:p.errorText,blockedReason:p.blockedReason})
        })
        await c.send('Page.navigate',{url:'https://admin.shopyxi.com/super-admin/orders'})
        await c.wait(`!!document.querySelector('input[type=date]')`)
        await c.wait(`!Array.from(document.querySelectorAll('button')).some(b=>['Refreshing','جارٍ التحديث'].includes(b.innerText))`)
        const state=()=>c.evaluate(`({
            path:location.pathname,visibility:document.visibilityState,online:navigator.onLine,focused:document.hasFocus(),
            monitoring:(document.querySelector('main')?.innerText||'').split('\\n')
                .filter(t=>/refresh|تحديث|unavailable|متاحة|stale|قديم|active|نشط|paused|متوقف/i.test(t)).slice(0,20)
        })`)
        const before=await state(),observedFrom=Date.now()
        await c.delay(65_000)
        const after=await state()
        const result={capturedAtUtc:new Date().toISOString(),actualHeadedBrowser:true,credentialsReadOrInjected:false,
            before,after,observedMs:Date.now()-observedFrom,requests:requests.filter(r=>r.at>=observedFrom),responses,failures}
        fs.writeFileSync(`${__dirname}/browser-runtime-diagnostics.json`,JSON.stringify(result,null,2)+'\n')
        console.log(JSON.stringify(result,null,2))
    }finally{c.close()}
}
main().catch(e=>{console.error(e.message);process.exitCode=1})
