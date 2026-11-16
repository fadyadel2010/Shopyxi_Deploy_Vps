// Headed, existing Chrome profile only. No session rewriting, response mocks or write actions.
const fs = require('node:fs')
const path = require('node:path')
const OUT = __dirname
const PRODUCT = 'b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8'
const VARIANT = '81069782-7bda-4e35-bf24-3e13424f06f2'
const LISTING = 'ff8cdce3-ad88-4467-b027-cdde5812e84d'
const RULE = '98eca7ca-414a-442f-bdf6-fb320cfb4de4'
const URL = 'https://admin.shopyxi.com/admin/commercehub'
const language = process.argv[2] || 'ar'
const phase = process.argv[3] || 'after'
const save = (name, data) => fs.writeFileSync(path.join(OUT, name), JSON.stringify(data, null, 2) + '\n')
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))

async function main() {
    const targets = await fetch('http://127.0.0.1:9224/json').then(r => r.json())
    const target = targets.find(t => t.type === 'page' && t.url.startsWith('https://admin.shopyxi.com/'))
    if (!target) throw new Error('Open the genuine Production session in headed Chrome on port 9224.')
    const ws = new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
    let id = 0
    const pending = new Map(), requests = new Map(), previewRequests = new Map()
    const responses = [], previews = [], writes = [], errors = [], failed = [], bodyReads = []
    function send(method, params = {}) {
        const requestId = ++id
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => { pending.delete(requestId); reject(new Error('CDP timeout: ' + method)) }, 20000)
            pending.set(requestId, {
                resolve: value => { clearTimeout(timeout); resolve(value) },
                reject: error => { clearTimeout(timeout); reject(error) },
            })
            ws.send(JSON.stringify({ id: requestId, method, params }))
        })
    }
    async function evaluate(expression) {
        const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text)
        return r.result.value
    }
    async function wait(expression) {
        const deadline = Date.now() + 45000
        while (Date.now() < deadline) {
            if (await evaluate(expression)) return
            await sleep(150)
        }
        throw new Error('Completed UI assertion timed out: ' + expression)
    }
    async function screenshot(name) {
        await send('Page.bringToFront')
        const m = await send('Page.getLayoutMetrics')
        const r = await send('Page.captureScreenshot', {
            format: 'png', captureBeyondViewport: true,
            clip: { x: 0, y: 0, width: Math.ceil(m.cssContentSize.width), height: Math.ceil(m.cssContentSize.height), scale: 1 },
        })
        fs.writeFileSync(path.join(OUT, name), Buffer.from(r.data, 'base64'))
    }
    ws.onmessage = event => {
        const m = JSON.parse(event.data)
        if (m.id) {
            const p = pending.get(m.id)
            if (p) { pending.delete(m.id); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result) }
            return
        }
        if (m.method === 'Network.requestWillBeSent') {
            const r = m.params.request
            const url = new globalThis.URL(r.url)
            requests.set(m.params.requestId, { method: r.method, path: url.pathname })
            if (url.pathname.startsWith('/api/') && !['GET', 'HEAD', 'OPTIONS'].includes(r.method)) {
                // Auth refresh/telemetry are recorded by path only, never bodies or headers.
                writes.push({ method: r.method, path: url.pathname })
            }
        }
        if (m.method === 'Network.responseReceived') {
            const info = requests.get(m.params.requestId)
            if (info?.path.startsWith('/api/')) {
                const record = { ...info, status: m.params.response.status }
                responses.push(record)
                if (record.status >= 400) failed.push(record)
                if (info.path.endsWith('/commission/resolve-preview') && record.status === 200)
                    previewRequests.set(m.params.requestId, record)
            }
        }
        if (m.method === 'Network.loadingFinished' && previewRequests.has(m.params.requestId)) {
            bodyReads.push(send('Network.getResponseBody', { requestId: m.params.requestId }).then(r => {
                const body = JSON.parse(r.base64Encoded ? Buffer.from(r.body, 'base64').toString() : r.body)
                previews.push({ ...previewRequests.get(m.params.requestId), body })
            }))
        }
        if (m.method === 'Network.loadingFailed') {
            const info = requests.get(m.params.requestId)
            if (info?.path.startsWith('/api/') && !m.params.canceled)
                failed.push({ ...info, error: m.params.errorText })
        }
        if (m.method === 'Runtime.exceptionThrown') errors.push({ kind: 'runtime', text: m.params.exceptionDetails.text })
        if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') errors.push({ kind: 'console.error' })
    }
    try {
        await send('Page.enable'); await send('Runtime.enable'); await send('Network.enable')
        await send('Page.bringToFront')
        await send('Page.navigate', { url: URL })
        await wait(`!!document.querySelector('main') || location.pathname.includes('login')`)
        if (await evaluate(`location.pathname.includes('login')`)) throw new Error('User must sign in personally.')
        const session = await evaluate(`fetch('/api/auth/session', {credentials:'include'}).then(r=>r.json()).then(s=>({role:s.user?.role,tenantId:s.user?.tenantId}))`)
        if (session.role !== 'Owner' || session.tenantId !== 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0')
            throw new Error('Expected authorized merchant Owner session; user action required.')
        if ((await evaluate(`document.documentElement.lang`)).split('-')[0] !== language) {
            await evaluate(`Array.from(document.querySelectorAll('header button')).find(b=>['EN','عربي'].includes(b.innerText.trim())).click()`)
        }
        await wait(`document.documentElement.lang.startsWith('${language}') && document.querySelector('main')?.innerText.includes('QA-ERP-20261112-TRACKED')`)
        const get = endpoint => evaluate(`fetch(${JSON.stringify(endpoint)},{credentials:'include',cache:'no-store'}).then(r=>{if(!r.ok)throw new Error('Read failed '+r.status);return r.json()})`)
        const before = await get('/api/products/' + PRODUCT)
        const catalogBefore = await get('/api/admin/commercehub/products')
        const variantBefore = catalogBefore.find(p => p.productId === PRODUCT)?.variants.find(v => v.variantId === VARIANT)
        if (variantBefore?.listing?.id !== LISTING && variantBefore?.listing?.listingId !== LISTING)
            throw new Error('Unexpected existing listing identity')
        if (variantBefore.listing.commerceHubPrice !== 12 || !variantBefore.listing.isPublished)
            throw new Error('Existing custom listing changed')
        await evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.innerText.includes('QA-ERP-20261112-TRACKED') && b.querySelector('svg')).click()`)
        await wait(`document.querySelectorAll('input[type=radio]').length===2`)
        const modes = {}
        // Read the persisted custom mode first. Do not edit its price input.
        for (const mode of ['custom', 'source']) {
            const index = mode === 'source' ? 0 : 1
            await evaluate(`document.querySelectorAll('input[type=radio]')[${index}].click()`)
            const price = mode === 'source' ? 10 : 12
            // Assert every completed numeric row and absence of loading, not an arbitrary delay.
            await wait(`(() => {
                const panel=Array.from(document.querySelectorAll('section')).find(s=>s.querySelector('h4'));
                if(!panel || panel.querySelector('.animate-spin'))return false;
                const values=Array.from(panel.querySelectorAll('dl dd')).map(d=>d.innerText.replace(/[\\u200e\\u200f\\u061c]/g,''));
                const format=n=>new Intl.NumberFormat('${language==='ar'?'ar-EG':'en-US'}',{style:'currency',currency:'EGP'}).format(n).replace(/[\\u200e\\u200f\\u061c]/g,'');
                return values.length===5 && values[0]===format(10) && values[1]===format(${price}) &&
                    values[2]===new Intl.NumberFormat('${language==='ar'?'ar-EG':'en-US'}').format(10)+'%' &&
                    values[3]==='− '+format(${price/10}) && values[4]===format(${price*0.9});
            })()`)
            await Promise.all(bodyReads)
            const response = [...previews].reverse().find(p => p.body.marketplacePrice === price)
            if (!response || response.body.rate !== 0.1 || response.body.ruleId !== RULE ||
                response.body.commissionAmount !== price / 10 || response.body.merchantNet !== price * 0.9 ||
                response.body.usedLegacyFallback !== false) throw new Error('Real UI response authority mismatch')
            const ui = await evaluate(`(() => {
                const panel=Array.from(document.querySelectorAll('section')).find(s=>s.querySelector('h4'));
                return {completed:true,loading:!!panel.querySelector('.animate-spin'),
                    numericRows:Array.from(panel.querySelectorAll('dl > div')).map(d=>({label:d.querySelector('dt').innerText,value:d.querySelector('dd').innerText})),
                    text:panel.innerText,selectedMode:Array.from(document.querySelectorAll('input[type=radio]')).findIndex(i=>i.checked)===0?'source':'custom',
                    direction:document.documentElement.dir,language:document.documentElement.lang,
                    horizontalOverflow:document.documentElement.scrollWidth>document.documentElement.clientWidth};
            })()`)
            const shot = `${phase}-${language}-${mode}-completed.png`
            await screenshot(shot)
            modes[mode] = { verifiedAtUtc: new Date().toISOString(), apiResponse: response, ui, screenshot: shot }
        }
        const rendered = await evaluate(`({
            mainText:document.querySelector('main').innerText,
            visibleLatinText:Array.from(document.querySelectorAll('body *')).filter(e=>{
                const r=e.getBoundingClientRect();
                return e.children.length===0 && !e.closest('[inert]') && e.checkVisibility({checkOpacity:true,checkVisibilityCSS:true}) &&
                    r.width>0 && r.right>0 && r.left<document.documentElement.clientWidth && /[A-Za-z]/.test(e.innerText||'');
            }).map(e=>e.innerText),
            roleText:document.querySelector('header .text-start .text-xs')?.innerText,
            helpText:Array.from(document.querySelectorAll('main button')).find(b=>b.querySelector('.lucide-circle-question-mark'))?.innerText,
            browserLanguage:navigator.language,direction:document.documentElement.dir,language:document.documentElement.lang
        })`)
        // Reload discards the unsaved source-mode selection; confirm custom price is still persisted.
        await send('Page.reload', { ignoreCache: true })
        await wait(`document.querySelector('main')?.innerText.includes('QA-ERP-20261112-TRACKED')`)
        const after = await get('/api/products/' + PRODUCT)
        const catalogAfter = await get('/api/admin/commercehub/products')
        const variantAfter = catalogAfter.find(p => p.productId === PRODUCT)?.variants.find(v => v.variantId === VARIANT)
        const protectedKeys = ['salePrice','purchasePrice','units','variants','warehouseStocks','taxRate','priceIncludesTax','isOnlineVisible','publishStatus']
        const changedProtectedFields = protectedKeys.filter(k => JSON.stringify(before[k]) !== JSON.stringify(after[k]))
        const listingUnchanged = JSON.stringify(variantBefore.listing) === JSON.stringify(variantAfter.listing)
        const businessWrites = writes.filter(w => !/^\/api\/(auth\/|telemetry)/.test(w.path))
        const result = {
            verifiedAtUtc: new Date().toISOString(), url: URL, production: true, actualHeadedBrowser: true,
            authentication: 'Existing genuine merchant cookie session; no injected credentials', session,
            language, phase, productId: PRODUCT, variantId: VARIANT, listingId: LISTING,
            modes, rendered, responses, errors, failed, nonGetRequests: writes, businessWrites,
            integrity: { changedProtectedFields, listingUnchanged, listing: variantAfter.listing,
                posPrice: after.salePrice, effectiveStorefrontPrice: after.variants[0].effectiveOnlinePrice,
                stock: after.variants[0].totalStock },
        }
        save(`browser-${phase}-${language}.json`, result)
        if (changedProtectedFields.length || !listingUnchanged || businessWrites.length || errors.length || failed.length)
            throw new Error('Integrity or browser errors; inspect saved evidence')
        console.log(JSON.stringify({ phase, language, modes: Object.keys(modes), rendered,
            integrity: result.integrity, errors, failed, businessWrites }, null, 2))
    } finally { ws.close() }
}
main().catch(error => { console.error(error.message); process.exitCode = 1 })
