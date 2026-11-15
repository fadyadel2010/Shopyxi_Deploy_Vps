async page => {
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 const result=await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const path='/api/shippingmethods/ffd597bb-6276-48ef-bbf7-869a151c2e20';const d=await(await fetch(path)).json();
  if(!d.isActive||d.carrierCode!=='manual')throw Error('Unexpected shipping fixture');
  const r=await fetch(path,{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,'If-Match':'"'+d.revision+'"'},body:JSON.stringify({...d,isActive:false})});
  if(r.status!==204)throw Error('Shipping disable failed');return {status:r.status,carrier:d.carrierCode};
 });
 await page.reload();await page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'}).getByRole('button').click();
 const p=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');await p.locator('[data-blocker-code=SHIPPING_METHOD_REQUIRED]').waitFor();
 await p.screenshot({path:'validation/affiliate-publishing-readiness/scenario-E-shipping.png'});
 return {scenario:'E',result,text:await p.innerText(),navigation:await p.getByRole('link').getAttribute('href')};
}
