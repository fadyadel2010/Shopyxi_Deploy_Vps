// Real headed Chrome/CDP; no login automation, cookie reads, token injection,
// response substitution, or business writes. The user authenticates personally.
const fs = require('node:fs')
const path = require('node:path')
const phase = process.argv[2] || 'before'
if (!/^[a-z-]+$/.test(phase)) throw new Error('Invalid evidence phase')
const out = name => path.join(__dirname, name)
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))

async function main() {
    const pages = await fetch('http://127.0.0.1:9222/json').then(r => r.json())
    const target = pages.find(p => p.type === 'page' && p.url.startsWith('https://admin.shopyxi.com/'))
    if (!target) throw new Error('Open headed Chrome at the production Admin origin first')
    const ws = new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
    let nextId = 0
    const pending = new Map()
    const requests = new Map()
    const finance = []
    const failures = []
    let consoleErrorCount = 0
    let exceptionCount = 0
    let businessWriteCount = 0
    function send(method, params = {}) {
        const id = ++nextId
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)) }, 30000)
            pending.set(id, { resolve, reject, timer })
            ws.send(JSON.stringify({ id, method, params }))
        })
    }
    ws.onmessage = async event => {
        const message = JSON.parse(event.data)
        if (message.id && pending.has(message.id)) {
            const p = pending.get(message.id)
            pending.delete(message.id)
            clearTimeout(p.timer)
            return message.error ? p.reject(new Error(message.error.message)) : p.resolve(message.result)
        }
        const p = message.params || {}
        if (message.method === 'Runtime.exceptionThrown') exceptionCount++
        if (message.method === 'Runtime.consoleAPICalled' && p.type === 'error') consoleErrorCount++
        if (message.method === 'Network.requestWillBeSent') {
            const url = new URL(p.request.url)
            requests.set(p.requestId, url.pathname)
            if (url.origin === 'https://admin.shopyxi.com' && url.pathname.startsWith('/api/') &&
                !url.pathname.includes('/auth/') && !['GET', 'HEAD', 'OPTIONS'].includes(p.request.method)) businessWriteCount++
        }
        if (message.method === 'Network.loadingFailed') {
            const requestPath = requests.get(p.requestId)
            if (requestPath?.startsWith('/api/super-admin/finance')) failures.push({ path: requestPath, failed: true })
        }
        if (message.method === 'Network.responseReceived' &&
            p.response.url.includes('/api/super-admin/finance/')) {
            const requestPath = new URL(p.response.url).pathname
            finance.push({ path: requestPath, status: p.response.status, requestId: p.requestId })
            if (p.response.status >= 400) failures.push({ path: requestPath, status: p.response.status })
        }
    }
    async function evaluate(expression) {
        const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
        if (r.exceptionDetails) throw new Error('Browser expression failed; no raw exception persisted')
        return r.result.value
    }
    async function waitFor(expression) {
        for (let i = 0; i < 100; i++) {
            if (await evaluate(expression)) return
            await delay(200)
        }
        throw new Error('Expected production UI state did not complete')
    }
    await send('Page.enable')
    await send('Runtime.enable')
    await send('Network.enable')
    await send('Page.bringToFront')
    const initialPath = await evaluate('location.pathname')
    if (initialPath.includes('/login')) {
        const blocked = { capturedAtUtc: new Date().toISOString(), actualHeadedBrowser: true,
            status: 'BLOCKED', path: initialPath, reason: 'USER_INTERACTIVE_SUPER_ADMIN_LOGIN_REQUIRED',
            credentialsReadOrInjected: false, businessWriteCount }
        fs.writeFileSync(out(`browser-${phase}.json`), JSON.stringify(blocked, null, 2) + '\n')
        console.log(JSON.stringify(blocked, null, 2))
        ws.close()
        return
    }
    await send('Page.navigate', { url: 'https://admin.shopyxi.com/super-admin/finance' })
    await waitFor(`location.pathname.includes('/login') || document.querySelectorAll('input[type=date]').length >= 2`)
    if ((await evaluate('location.pathname')).includes('/login')) throw new Error('User login is required')
    await waitFor(`!Array.from(document.querySelectorAll('button')).some(b => b.innerText === 'Refreshing' || b.innerText === 'جارٍ التحديث')`)
    const captures = []
    for (const window of ['default', 'historical']) {
      if (window === 'historical') {
        await evaluate(`(() => {
            const dates = Array.from(document.querySelectorAll('input[type=date]')).slice(0,2)
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set
            for (const [index, value] of ['2026-10-18','2026-11-16'].entries()) {
                setter.call(dates[index], value)
                dates[index].dispatchEvent(new Event('input', { bubbles: true }))
                dates[index].dispatchEvent(new Event('change', { bubbles: true }))
            }
        })()`)
        await delay(800)
        await waitFor(`!Array.from(document.querySelectorAll('button')).some(b => b.innerText === 'Refreshing' || b.innerText === 'جارٍ التحديث')`)
      }
      for (const language of ['en', 'ar']) {
        const direction = language === 'ar' ? 'rtl' : 'ltr'
        if (await evaluate('document.documentElement.dir') !== direction) {
            await evaluate(`Array.from(document.querySelectorAll('button')).find(b => ['Switch to Arabic', 'التبديل إلى الإنجليزية'].includes(b.title)).click()`)
            await waitFor(`document.documentElement.dir === '${direction}'`)
        }
        for (const width of [1440, 390]) {
            await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: width < 500 })
            await delay(400)
            const capture = await evaluate(`({
                language: document.documentElement.lang, direction: document.documentElement.dir,
                width: innerWidth, horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
                reportingInputs: Array.from(document.querySelectorAll('input[type=date]')).slice(0,2).map(i=>i.value),
                rawIntegrityEnumVisible: /NOT_APPLICABLE|ISSUES_DETECTED/.test(document.querySelector('main')?.innerText || ''),
                hasVisibleError: !!document.querySelector('[role=alert]')
            })`)
            capture.window = window
            const rect = await evaluate(`(() => {
                const r = document.querySelector('main').getBoundingClientRect()
                return { x: Math.max(0,r.x), y: Math.max(0,r.y) + scrollY,
                    width: Math.min(r.width,innerWidth-Math.max(0,r.x)), height: Math.min(1000,r.height) }
            })()`)
            const screenshot = await send('Page.captureScreenshot', { format: 'png',
                captureBeyondViewport: true, clip: { ...rect, scale: 1 } })
            capture.screenshot = `finance-${phase}-${window}-${language}-${width}.png`
            fs.writeFileSync(out(capture.screenshot), Buffer.from(screenshot.data, 'base64'))
            captures.push(capture)
        }
      }
    }
    await send('Emulation.clearDeviceMetricsOverride')
    const sanitized = []
    for (const response of finance) {
        const entry = { path: response.path, status: response.status }
        if (response.status === 200 && response.path.endsWith('/overview')) {
            try {
                const body = await send('Network.getResponseBody', { requestId: response.requestId })
                const json = JSON.parse(body.base64Encoded ? Buffer.from(body.body, 'base64').toString() : body.body)
                const data = json.data
                entry.allRequiredMetricsZero = [
                    'totalGrossCommission', 'totalPromoCost', 'totalNetCommission', 'netRevenue',
                    'totalRefundAmount', 'averageOrderValue', 'totalOrdersCount', 'refundRate', 'refundImpactOnRevenue',
                ].every(key => data?.[key] === 0)
                entry.source = data?.dataSource
                entry.fromDateUtc = data?.fromDateUtc
                entry.toDateUtc = data?.toDateUtc
            } catch { entry.responseInspection = 'UNAVAILABLE' }
        }
        sanitized.push(entry)
    }
    const required = ['overview','trends','promos','vendors','refunds','integrity-check']
    const checksPassed = captures.length === 8 && captures.every(c =>
        !c.horizontalOverflow && !c.rawIntegrityEnumVisible && !c.hasVisibleError &&
        c.direction === (c.language === 'ar' ? 'rtl' : 'ltr') &&
        (c.window !== 'historical' || c.reportingInputs.join(',') === '2026-10-18,2026-11-16')) &&
        required.every(endpoint => sanitized.some(r => r.path.endsWith('/'+endpoint) && r.status === 200)) &&
        sanitized.filter(r => r.path.endsWith('/overview')).every(r => r.allRequiredMetricsZero) &&
        !failures.length && !consoleErrorCount && !exceptionCount && !businessWriteCount
    const result = { capturedAtUtc: new Date().toISOString(), actualHeadedBrowser: true,
        authenticatedByUser: true, credentialsReadOrInjected: false, sourcePhase: phase,
        status: phase === 'after' && checksPassed ? 'PASS_BROWSER_EMPTY_PRODUCTION_SCOPE' : 'OBSERVED_NOT_RELEASE_ACCEPTANCE',
        checksPassed, captures, responses: sanitized,
        failedRequiredRequests: failures, consoleErrorCount, exceptionCount, businessWriteCount }
    fs.writeFileSync(out(`browser-${phase}.json`), JSON.stringify(result, null, 2) + '\n')
    console.log(JSON.stringify(result, null, 2))
    ws.close()
}
main().catch(error => { console.error(error.message); process.exitCode = 1 })
