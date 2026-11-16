async (page) => {
  await page.goto('http://admin.localhost:18180/admin/affiliates/products')
  await page.waitForLoadState('networkidle')
  return {origin:await page.evaluate(()=>location.origin),route:await page.evaluate(()=>location.pathname+location.search),
    headings:await page.locator('h1,h2').allTextContents(),
    storageKeyNames:await page.evaluate(()=>Object.keys(localStorage)),
    cookieNames:await page.context().cookies().then(xs=>xs.map(x=>x.name).sort())}
}
