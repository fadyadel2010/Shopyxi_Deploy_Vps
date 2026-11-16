async (page) => {
  const responses=[]
  page.on('response',r=>{if(/\/api\/(storefront\/cart|storefront\/checkout|checkout)/.test(r.url()))responses.push({path:r.url().replace(/^https?:\/\/[^/]+/,''),status:r.status()})})
  if (!page.url().includes('/products/qa-ahram-affiliate-e2e-20261115'))
    await page.goto('http://ahram.localhost:18181/products/qa-ahram-affiliate-e2e-20261115?ref=WWC3NC7R9B')
  await page.getByRole('button',{name:'Add to Cart',exact:true}).click()
  await page.waitForTimeout(1000)
  await page.goto('http://ahram.localhost:18181/checkout')
  await page.waitForLoadState('networkidle')
  const values={fullName:'Synthetic Affiliate Customer',phone:'1000000001',email:'affiliate-checkout@example.invalid',city:'Cairo',area:'Synthetic QA',addressLine:'1 Isolated Test Street',building:'1',apartment:'1',postalCode:'11511',notes:'Isolated affiliate conversion bridge acceptance'}
  for (const [name,value] of Object.entries(values)) {
    const field=page.locator(`[name="${name}"]`).first()
    if(await field.isVisible().catch(()=>false)) await field.fill(value)
  }
  const shipping=page.locator('input[name="shippingMethod"]:not([disabled])').first()
  await shipping.waitFor({state:'attached'})
  await shipping.check()
  await page.waitForTimeout(1000)
  const continueButton=page.getByRole('button',{name:'Continue to Payment',exact:true})
  await continueButton.click()
  const cod=page.locator('input[name="paymentMethod"][value="COD"]')
  await cod.waitFor({state:'attached'})
  await cod.check()
  await page.waitForTimeout(500)
  await page.getByRole('button',{name:'Review Order',exact:true}).click()
  await page.getByRole('button',{name:'Place Order',exact:true}).waitFor({state:'visible'})
  await page.waitForTimeout(1500)
  const place=page.getByRole('button',{name:'Place Order',exact:true})
  return {route:await page.evaluate(()=>location.pathname+location.search),placeOrderEnabled:await place.isEnabled(),
    referralBanner:await page.getByText(/browsing via a partner referral/).isVisible().catch(()=>false),responses,
    body:(await page.locator('body').innerText()).replace(/\s+/g,' ').slice(0,1600)}
}
