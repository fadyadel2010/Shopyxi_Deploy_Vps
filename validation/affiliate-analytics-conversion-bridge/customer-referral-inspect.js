async (page) => {
  const responses=[]
  page.on('response',r=>{if(r.url().includes('/api/'))responses.push({path:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status()})})
  await page.context().clearCookies()
  await page.goto('http://ahram.localhost:18181/products/qa-ahram-affiliate-e2e-20261115?ref=WWC3NC7R9B')
  await page.waitForLoadState('networkidle')
  return {route:await page.evaluate(()=>location.pathname+location.search),
    headings:await page.locator('h1,h2').allTextContents(),
    actions:(await page.getByRole('button').allTextContents()).filter(x=>x.trim()).slice(0,30),
    body:(await page.locator('body').innerText()).replace(/\s+/g,' ').slice(0,1000),responses}
}
