async page => {
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 const before=await page.evaluate(async()=>await(await fetch('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate')).json());
 if(!before.readiness.isPromotionEligible)throw Error('Fixture not ready');
 const status=await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const r=await fetch('/api/admin/payment-methods/COD/disable',{method:'POST',headers:{'X-CSRF-Token':csrf}});return r.status;
 });if(status!==200)throw Error('COD disable failed');
 await page.reload();
 await page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'}).getByRole('button').click();
 const p=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');await p.locator('[data-blocker-code=PAYMENT_METHOD_REQUIRED]').waitFor();
 await p.screenshot({path:'validation/affiliate-publishing-readiness/scenario-D-payment.png'});
 return {scenario:'D',method:'supported COD disable API, headed Manage UI',status,text:await p.innerText(),navigation:await p.getByRole('link').getAttribute('href')};
}
