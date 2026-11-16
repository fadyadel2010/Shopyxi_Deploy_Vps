async (page) => {
  page.removeAllListeners('response');page.removeAllListeners('pageerror')
  const errors=[];page.on('pageerror',e=>errors.push(e.name))
  await page.goto('https://admin.shopyxi.com/admin/affiliates/products')
  await page.waitForLoadState('networkidle')
  const result=await page.evaluate(async()=>{
    const r=await fetch('/api/admin/affiliate-analytics/conversions/reconciliation',{credentials:'include'})
    return {status:r.status,body:r.status===200?await r.json():null}
  })
  return {origin:await page.evaluate(()=>location.origin),route:await page.evaluate(()=>location.pathname),
    authenticatedAdmin:await page.getByRole('heading',{name:'Affiliate Products',exact:true}).isVisible().catch(()=>false),
    reconciliation:result,errors,readOnly:true}
}
