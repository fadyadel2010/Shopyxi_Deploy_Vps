async (page) => {
  page.removeAllListeners('response');page.removeAllListeners('console');page.removeAllListeners('pageerror')
  const errors=[],responses=[]
  page.on('pageerror',e=>errors.push({type:'pageerror',name:e.name}))
  page.on('console',m=>{if(m.type()==='error')errors.push({type:'console',text:m.text().slice(0,160)})})
  page.on('response',async r=>{
    if(/\/api\/affiliate\/analytics\/(summary|trend)(\?|$)/.test(r.url())) {
      let body=null
      if(r.status()===200) try{body=await r.json()}catch{}
      responses.push({path:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status(),body})
    }
  })
  await page.goto('http://affiliate.localhost:18180/affiliate/portal?tab=analytics')
  await page.waitForLoadState('networkidle')
  await page.getByText('Performance Analytics',{exact:true}).waitFor({state:'visible'})
  const thirtyDayText=(await page.locator('body').innerText()).replace(/\s+/g,' ')
  const kpis={}
  for(const label of ['Tracked Click Events','Attributed Orders','Total Earnings','Conversion Rate'])
    kpis[label]=await page.getByText(label,{exact:true}).locator('xpath=../..').locator('p').first().innerText()
  const apiChecks=await page.evaluate(async()=>{
    const get=async path=>{const r=await fetch(path,{credentials:'include'});return {status:r.status,body:await r.json()}}
    const tenant='15150000-2026-4015-a000-000000000001',product='37d9a1b6-6585-f655-0e55-824a517ce90c'
    return {
      tenant:await get('/api/affiliate/analytics/summary?range=30&tenantId='+tenant),
      otherTenant:await get('/api/affiliate/analytics/summary?range=30&tenantId=25150000-2026-4015-a000-000000000001'),
      product:await get('/api/affiliate/analytics/summary?range=30&productId='+product),
      productTrend:await get('/api/affiliate/analytics/trend?range=30&productId='+product),
      otherProductTrend:await get('/api/affiliate/analytics/trend?range=30&productId=47d9a1b6-6585-f655-0e55-824a517ce90c')
    }
  })
  await page.getByRole('button',{name:'7 Days',exact:true}).click();await page.waitForLoadState('networkidle')
  await page.evaluate(()=>window.dispatchEvent(new Event('online')));await page.waitForTimeout(1500)
  await page.reload();await page.waitForLoadState('networkidle')
  const definition=await page.getByTestId('conversion-definition').innerText()
  const root=page.getByText('Performance Analytics',{exact:true}).locator('xpath=../..')
  await root.screenshot({path:'C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115/source/validation/affiliate-analytics-conversion-bridge/analytics-after-filter-final.png'})
  return {route:await page.evaluate(()=>location.pathname+location.search),
    thirtyDaySemantics:{kpis,trackedClickEventsLabeled:kpis['Tracked Click Events']!=='',attributedOrders:kpis['Attributed Orders']==='2',conversionRate:kpis['Conversion Rate']==='50.00%'},
    definition,apiChecks,responses,errors:errors.filter(e=>!e.text?.includes('ERR_BLOCKED_BY_CLIENT.Inspector')),
    harnessBlockedResources:errors.filter(e=>e.text?.includes('ERR_BLOCKED_BY_CLIENT.Inspector')).length}
}
