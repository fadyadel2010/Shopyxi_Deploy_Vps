async (page) => {
  const errors = []
  page.on('pageerror', e => errors.push(e.name))
  const responses = []
  page.on('response', async r => {
    if (/\/api\/affiliate\/analytics\/(summary|trend)(\?|$)/.test(r.url())) {
      responses.push({pathAndQuery:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status(),body:await r.json()})
    }
  })
  await page.getByRole('button', {name:'Analytics',exact:true}).click()
  await page.getByText('Performance Analytics', {exact:true}).waitFor()
  await page.waitForResponse(r => /\/analytics\/summary\?/.test(r.url()) && r.status() === 200)
  await page.locator('.animate-pulse').first().waitFor({state:'hidden'})
  // Mask shell identity: analytics area contains no customer details.
  const area = page.locator('div.space-y-6').filter({has:page.getByRole('heading',{name:'Performance Analytics'})}).last()
  await area.screenshot({path:'C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115/source/validation/affiliate-analytics-conversion-bridge/analytics-before.png'})
  return {route:await page.evaluate(()=>location.pathname+location.search),responses,errors}
}
