async page => {
 if(!page.url().startsWith('http://admin.localhost:18180/'))throw Error('Isolated Owner only');
 const mutations=await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const json=async(path,method='GET',body,headers={})=>{const r=await fetch(path,{method,headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,...headers},body:body===undefined?undefined:JSON.stringify(body)});let data=null;if(r.status!==204)data=await r.json();if(!r.ok)throw Error(path+' '+r.status);return data};
  const config=await json('/api/admin/store-config');
  const shipping=(await json('/api/shippingmethods'))[0];
  if(config.storefrontStatus!=='live'||!shipping.isActive)throw Error('Unexpected initial fixture');
  await json('/api/admin/store-config/status','PUT',{storefrontStatus:'disabled',rowVersion:config.rowVersion});
  await json('/api/admin/payment-methods/COD/disable','POST');
  await json('/api/shippingmethods/'+shipping.id,'PUT',{...shipping,isActive:false},{'If-Match':'"'+shipping.revision+'"'});
  return {storefront:'disabled',payment:'COD disabled',shipping:'manual inactive'};
 });
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 const row=page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'});
 await row.getByRole('button',{name:'Manage: QA-AHRAM-AFFILIATE-E2E-20261115',exact:true}).click();
 const panel=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');
 await panel.locator('[data-blocker-code=SHIPPING_METHOD_REQUIRED]').waitFor();
 const data=await page.evaluate(async()=>await(await fetch('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate')).json());
 await panel.screenshot({path:'validation/affiliate-publishing-readiness/scenario-G-blockers.png'});
 return {scenario:'G',mutations,readiness:data.readiness,isPolicyEnabled:data.isPolicyEnabled,commissionRate:data.commissionRate,
  panel:await panel.innerText(),links:await panel.getByRole('link').evaluateAll(xs=>xs.map(a=>a.getAttribute('href')))};
}
