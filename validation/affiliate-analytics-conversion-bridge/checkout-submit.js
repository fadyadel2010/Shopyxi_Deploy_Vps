async (page) => {
  if (!page.url().includes('/checkout')) throw new Error('prepared checkout page required')
  const responses=[]
  page.on('response',async r=>{if(/\/api\/storefront\/checkout/.test(r.url()))responses.push({path:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status()})})
  const place=page.getByRole('button',{name:'Place Order',exact:true})
  if(!await place.isEnabled()) throw new Error('prepared order is not submit-ready')
  await place.click()
  await page.getByText('Order saved — cash on delivery pending',{exact:true}).waitFor({state:'visible',timeout:90000})
  return {route:await page.evaluate(()=>location.pathname+location.search),responses,
    confirmation:(await page.locator('body').innerText()).replace(/\s+/g,' ').slice(0,1300)}
}
