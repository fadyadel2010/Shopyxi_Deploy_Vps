const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
async function connect() {
    const targets = await fetch('http://127.0.0.1:9222/json').then(r => r.json())
    const target = targets.find(p => p.type === 'page' && p.url.startsWith('https://admin.shopyxi.com/'))
    if (!target) throw Error('Production headed Chrome is not available')
    const ws = new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
    let id = 0
    const pending = new Map(), listeners = []
    const send = (method, params = {}) => new Promise((resolve, reject) => {
        const current = ++id
        const timer = setTimeout(() => { pending.delete(current); reject(Error(`CDP timeout: ${method}`)) }, 30000)
        pending.set(current, { resolve, reject, timer })
        ws.send(JSON.stringify({ id: current, method, params }))
    })
    ws.onmessage = event => {
        const m = JSON.parse(event.data)
        const call = pending.get(m.id)
        if (call) {
            pending.delete(m.id); clearTimeout(call.timer)
            return m.error ? call.reject(Error(m.error.message)) : call.resolve(m.result)
        }
        for (const listen of listeners) listen(m.method, m.params || {})
    }
    const evaluate = async expression => {
        const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
        if (r.exceptionDetails) throw Error('Browser expression failed; private text not retained')
        return r.result.value
    }
    const wait = async expression => {
        for (let i = 0; i < 150; i++) {
            if (await evaluate(expression)) return
            await delay(200)
        }
        throw Error('Browser condition not satisfied')
    }
    await send('Page.enable'); await send('Runtime.enable'); await send('Network.enable')
    await send('Page.bringToFront')
    return { send, evaluate, wait, delay, listeners, close: () => ws.close() }
}
module.exports = { connect }
