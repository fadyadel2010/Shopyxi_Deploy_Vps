// Restore the actual headed window; do not override document visibility or focus.
const {connect}=require('./headed-cdp.cjs')
async function main(){
    const c=await connect()
    try{
        if(process.argv[2]==='close'){
            await c.send('Browser.close')
            return
        }
        const window=await c.send('Browser.getWindowForTarget')
        console.log(JSON.stringify({beforeWindow:window.bounds}))
        if(window.bounds.windowState==='minimized')
            await c.send('Browser.setWindowBounds',{windowId:window.windowId,bounds:{windowState:'normal'}})
        await c.send('Page.bringToFront')
        console.log(JSON.stringify(await c.evaluate(`({visibility:document.visibilityState,online:navigator.onLine,focused:document.hasFocus()})`)))
    }finally{c.close()}
}
main().catch(e=>{console.error(e.message);process.exitCode=1})
