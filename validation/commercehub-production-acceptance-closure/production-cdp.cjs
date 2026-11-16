// Attach only to the user's existing headed Production tab.
exports.connect = async function () {
    const targets = await fetch('http://127.0.0.1:9224/json').then(r => r.json())
    const target = targets.find(t => t.type === 'page' && t.url.startsWith('https://admin.shopyxi.com/'))
    if (!target) throw new Error('Genuine Production browser tab unavailable')
    const ws = new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
    let id = 0
    const pending = new Map()
    ws.onmessage = event => {
        const message = JSON.parse(event.data)
        const item = pending.get(message.id)
        if (item) {
            pending.delete(message.id)
            message.error ? item.reject(new Error(message.error.message)) : item.resolve(message.result)
        }
    }
    function send(method, params = {}) {
        const requestId = ++id
        return new Promise((resolve, reject) => {
            pending.set(requestId, { resolve, reject })
            ws.send(JSON.stringify({ id: requestId, method, params }))
        })
    }
    return {
        send,
        evaluate: async expression => {
            const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
            if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text)
            return result.result.value
        },
        close: () => ws.close(),
    }
}
