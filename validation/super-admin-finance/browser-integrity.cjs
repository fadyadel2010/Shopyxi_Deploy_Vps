// Read-only integrity presentation evidence from the real authenticated headed
// browser. Saves only coverage labels, aggregate counts, and a card screenshot.
const fs = require('node:fs')
const path = require('node:path')
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))

async function main() {
    const pages = await fetch('http://127.0.0.1:9222/json').then(r => r.json())
    const target = pages.find(p => p.type === 'page' && p.url.startsWith('https://admin.shopyxi.com/'))
    if (!target) throw new Error('Open headed Chrome at the production Admin origin first')
    const ws = new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
    let nextId = 0
    const pending = new Map()
    function send(method, params = {}) {
        const id = ++nextId
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)) }, 30000)
            pending.set(id, { resolve, reject, timer })
            ws.send(JSON.stringify({ id, method, params }))
        })
    }
    ws.onmessage = event => {
        const message = JSON.parse(event.data)
        if (message.id && pending.has(message.id)) {
            const p = pending.get(message.id)
            pending.delete(message.id)
            clearTimeout(p.timer)
            return message.error ? p.reject(new Error(message.error.message)) : p.resolve(message.result)
        }
    }
    async function evaluate(expression) {
        const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
        if (r.exceptionDetails) throw new Error('Browser expression failed')
        return r.result.value
    }
    async function waitFor(expression) {
        for (let i = 0; i < 100; i++) {
            if (await evaluate(expression)) return
            await delay(200)
        }
        throw new Error('Expected production UI state did not complete')
    }
    async function capture(language, direction, filename) {
        if (await evaluate('document.documentElement.dir') !== direction) {
            await evaluate(`Array.from(document.querySelectorAll('button')).find(b => ['Switch to Arabic', 'التبديل إلى الإنجليزية'].includes(b.title)).click()`)
            await waitFor(`document.documentElement.dir === '${direction}'`)
        }
        const accountChromeHiddenForPrivacy = await evaluate(`(() => {
            const logout = Array.from(document.querySelectorAll('button')).find(b => /Log out|تسجيل الخروج/.test(b.textContent || ''))
            if (!logout) return false
            const bar = logout.closest('header') || logout.parentElement?.parentElement?.parentElement
            if (!bar) return false
            bar.style.visibility = 'hidden'
            return true
        })()`)
        await evaluate(`(() => {
            const heading = Array.from(document.querySelectorAll('h2')).find(h => h.textContent.trim().startsWith(${JSON.stringify(
                language === 'en' ? 'Ledger integrity and reference-level consistency' : 'سلامة دفتر الأستاذ والاتساق على مستوى المراجع')}))
            const section = heading && heading.closest('section')
            if (!section) return false
            section.scrollIntoView({ block: 'start' })
            return true
        })()`)
        await delay(500)
        const text = await evaluate(`(() => {
            const heading = Array.from(document.querySelectorAll('h2')).find(h => h.textContent.trim().startsWith(${JSON.stringify(
                language === 'en' ? 'Ledger integrity and reference-level consistency' : 'سلامة دفتر الأستاذ والاتساق على مستوى المراجع')}))
            return heading?.closest('section')?.innerText || ''
        })()`)
        const rect = await evaluate(`(() => {
            const heading = Array.from(document.querySelectorAll('h2')).find(h => h.textContent.trim().startsWith(${JSON.stringify(
                language === 'en' ? 'Ledger integrity and reference-level consistency' : 'سلامة دفتر الأستاذ والاتساق على مستوى المراجع')}))
            const r = heading.closest('section').getBoundingClientRect()
            return { x: Math.max(0, r.x), y: Math.max(0, r.y) + scrollY, width: Math.min(r.width, innerWidth - Math.max(0, r.x)), height: Math.min(r.height, 1400) }
        })()`)
        const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true,
            clip: { ...rect, scale: 1 } })
        fs.writeFileSync(path.join(__dirname, filename), Buffer.from(shot.data, 'base64'))
        return {
            language, direction, screenshot: filename, accountChromeHiddenForPrivacy,
            signedInEmailVisible: await evaluate(`(() => {
                const re = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}/;
                const el = Array.from(document.querySelectorAll('span,p,div')).find(e => e.children.length === 0 && re.test(e.textContent || ''));
                if (!el) return false;
                for (let e = el; e; e = e.parentElement) {
                    const s = getComputedStyle(e);
                    if (s.display === 'none' || s.visibility === 'hidden') return false;
                }
                return el.getClientRects().length > 0;
            })()`),
            noRecordsLabel: language === 'en' ? text.includes('No records checked') : text.includes('لم تُفحص سجلات'),
            limitedCoverageNotice: language === 'en'
                ? text.includes('Global balance and duplicate-pair checks are not included')
                : text.includes('لا يشمل هذا الفحص المصفّى'),
            notCheckedLabels: (text.match(language === 'en' ? /Not checked/g : /لم يُفحص/g) || []).length,
            rawEnumVisible: /NOT_APPLICABLE|ISSUES_DETECTED|NO_RECORDS/.test(text),
            visibleError: /Integrity check unavailable|فحص السلامة غير متاح/.test(text),
        }
    }
    await send('Page.enable')
    await send('Runtime.enable')
    await send('Network.enable')
    await send('Network.setCacheDisabled', { cacheDisabled: true })
    await send('Page.bringToFront')
    await send('Page.navigate', { url: 'https://admin.shopyxi.com/super-admin/finance' })
    await waitFor(`location.pathname.includes('/login') || document.querySelectorAll('input[type=date]').length >= 2`)
    if ((await evaluate('location.pathname')).includes('/login')) throw new Error('User login is required')
    await waitFor(`!Array.from(document.querySelectorAll('button')).some(b => b.innerText === 'Refreshing' || b.innerText === 'جارٍ التحديث')`)
    await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false })
    await delay(300)
    const english = await capture('en', 'ltr', 'finance-after-integrity-en-1440.png')
    const arabic = await capture('ar', 'rtl', 'finance-after-integrity-ar-1440.png')
    const integrity = await evaluate(`(async () => {
        const dates = Array.from(document.querySelectorAll('input[type=date]')).slice(0, 2).map(i => i.value)
        const params = new URLSearchParams({ maxIssues: '10' })
        if (dates[0]) params.set('fromDateUtc', dates[0] + 'T00:00:00Z')
        if (dates[1]) params.set('toDateUtc', dates[1] + 'T23:59:59.999Z')
        const response = await fetch('/api/super-admin/finance/integrity-check?' + params, { credentials: 'include' })
        const json = await response.json()
        const d = json.data || {}
        return { httpStatus: response.status, status: d.status,
            includesGlobalLedgerChecks: d.includesGlobalLedgerChecks,
            totalIssueCount: d.totalIssueCount, issueCount: (d.issues || []).length,
            counters: Object.fromEntries(Object.entries(d).filter(([k, v]) => k.endsWith('Count') && typeof v === 'number')) }
    })()`)
    const result = {
        capturedAtUtc: new Date().toISOString(), actualHeadedBrowser: true,
        credentialsReadOrInjected: false, sourcePhase: 'after',
        english, arabic, integrityApi: integrity,
        passed: !english.signedInEmailVisible && !arabic.signedInEmailVisible &&
            english.noRecordsLabel && arabic.noRecordsLabel && english.limitedCoverageNotice &&
            arabic.limitedCoverageNotice && !english.rawEnumVisible && !arabic.rawEnumVisible &&
            !english.visibleError && !arabic.visibleError && integrity.httpStatus === 200 &&
            integrity.status === 'NO_RECORDS' && integrity.includesGlobalLedgerChecks === false &&
            integrity.totalIssueCount === 0 && integrity.issueCount === 0 &&
            Object.values(integrity.counters).every(value => value === 0),
    }
    await send('Emulation.clearDeviceMetricsOverride')
    fs.writeFileSync(path.join(__dirname, 'browser-integrity-after.json'), JSON.stringify(result, null, 2) + '\n')
    console.log(JSON.stringify(result, null, 2))
    if (!result.passed) process.exitCode = 1
    ws.close()
}
main().catch(error => { console.error(error.message); process.exitCode = 1 })
