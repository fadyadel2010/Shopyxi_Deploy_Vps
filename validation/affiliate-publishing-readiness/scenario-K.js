async page => {
 const rows=[];
 async function panel(){
  await page.goto('http://admin.localhost:18180/admin/affiliates/products');
  const row=page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'});
  await row.getByRole('button').click();
  const p=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');
  await p.locator('[data-blocker-code=SHIPPING_METHOD_REQUIRED]').waitFor();return p;
 }
 let p=await panel();
 rows.push({language:'en',text:await p.innerText(),sections:await p.locator('[data-readiness-area]').count()});
 await page.getByRole('dialog').getByRole('button',{name:'Cancel',exact:true}).click();
 await page.getByRole('button',{name:'AR',exact:true}).click();
 p=await panel();
 await p.getByText('غير جاهز للترويج بالعمولة',{exact:true}).waitFor();
 await p.screenshot({path:'validation/affiliate-publishing-readiness/scenario-K-arabic.png'});
 rows.push({language:'ar',text:await p.innerText(),sections:await p.locator('[data-readiness-area]').count(),dir:await page.locator('html').getAttribute('dir')});
 const navigation=[];
 for(const path of ['/storefront/status','/ecommerce/payment-methods','/ecommerce/shipping-methods']){
  p=await panel();await p.locator('a[href="'+path+'"]').first().click();await page.waitForURL('http://admin.localhost:18180'+path);
  const identity=await page.evaluate(async()=>{const d=await(await fetch('/api/auth/session')).json();return {tenantId:d.user?.tenantId,role:d.user?.role}});
  if(identity.tenantId!=='15150000-2026-4015-a000-000000000001')throw Error('Tenant context changed');
  navigation.push({path,identity});
 }
 await page.getByRole('button',{name:'EN',exact:true}).click();
 return {scenario:'K',rows,navigation,languageRestored:'en'};
}
