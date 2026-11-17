// Observe the actual unmodified production UI export, not a direct API substitute.
const fs = require('node:fs')
const { connect } = require('./headed-cdp.cjs')
async function main() {
    const cdp = await connect(), requests = [], sent = []
    cdp.listeners.push((method,p) => {
        if (method === 'Network.requestWillBeSent' && p.request.url.includes('/orders/export'))
            sent.push({path:new URL(p.request.url).pathname,method:p.request.method})
        if (method === 'Network.responseReceived' && p.response.url.includes('/orders/export'))
            requests.push({ path: new URL(p.response.url).pathname, status: p.response.status, mimeType: p.response.mimeType })
    })
    // No persistent download is needed to prove a wrong MIME/path.
    await cdp.send('Page.setDownloadBehavior', {behavior:'deny'})
    await cdp.evaluate(`Array.from(document.querySelectorAll('button')).find(b=>['Export CSV','تصدير CSV'].includes(b.innerText)).click()`)
    await cdp.wait(`!Array.from(document.querySelectorAll('button')).some(b=>['Exporting CSV','جارٍ التصدير'].includes(b.innerText))`)
    await cdp.delay(1000)
    const result = {capturedAtUtc:new Date().toISOString(),actualHeadedBrowser:true,credentialsReadOrInjected:false,
        action:'CLICK_EXISTING_EXPORT_BUTTON',sent,requests,expectedApiPath:'/api/super-admin/orders/export',
        defectReproduced:requests.some(r=>r.path==='/orders/export' || !r.mimeType.includes('csv'))}
    fs.writeFileSync(`${__dirname}/export-before-complete.json`,JSON.stringify(result,null,2)+'\n')
    console.log(JSON.stringify(result,null,2));cdp.close()
}
main().catch(e=>{console.error(e.message);process.exitCode=1})
