async (page) => {
  const orderId = '8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12'
  await page.goto('http://admin.localhost:18180/admin/affiliates/products')
  await page.waitForLoadState('networkidle')
  if (!page.url().startsWith('http://admin.localhost:18180/'))
    throw new Error('existing owner session is no longer authenticated')
  const call = async (method, path) => page.evaluate(async ({method,path}) => {
    const csrf = decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.split('=')[1] || '')
    const r = await fetch(path,{method,credentials:'include',headers:csrf?{'X-CSRF-Token':csrf}:{}})
    return {status:r.status,body:await r.json()}
  }, {method,path})
  const before = await call('GET','/api/admin/affiliate-analytics/conversions/reconciliation')
  if (before.status !== 200) throw new Error('read-only candidate inspection failed')
  const repaired = await call('POST','/api/admin/affiliate-analytics/conversions/reconciliation/'+orderId)
  if (repaired.status !== 200) throw new Error('bounded projection repair failed')
  const replay = await call('POST','/api/admin/affiliate-analytics/conversions/reconciliation/'+orderId)
  const after = await call('GET','/api/admin/affiliate-analytics/conversions/reconciliation')
  return {before,repaired,replay,after,sqlBusinessMutationsRequested:false,
    scope:'analytics projection for one explicit existing isolated order'}
}
