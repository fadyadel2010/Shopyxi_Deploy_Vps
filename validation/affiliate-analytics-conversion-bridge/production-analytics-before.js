async (page) => {
  const responses=[], errors=[]
  page.on('pageerror',e=>errors.push(e.name))
  page.on('response',async r=>{
    if(/\/api\/affiliate\/analytics\/(summary|trend)(\?|$)/.test(r.url()))
      responses.push({pathAndQuery:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status(),body:r.status()===200?await r.json():null})
  })
  await page.goto('https://affiliate.shopyxi.com/affiliate/portal?tab=analytics')
  await page.waitForLoadState('networkidle')
  return {origin:await page.evaluate(()=>location.origin),route:await page.evaluate(()=>location.pathname+location.search),
    authenticatedPortal:await page.getByText('Performance Analytics',{exact:true}).isVisible().catch(()=>false),responses,errors}
}
