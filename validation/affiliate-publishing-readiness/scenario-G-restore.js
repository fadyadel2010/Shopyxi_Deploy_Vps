async page => {
 if(!page.url().startsWith('http://admin.localhost:18180/'))throw Error('Isolated Owner only');
 const results=await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const json=async(path,method='GET',body)=>{const r=await fetch(path,{method,headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:body===undefined?undefined:JSON.stringify(body)});if(!r.ok)throw Error(path+' '+r.status);return r.status===204?null:await r.json()};
  const read=async()=>{const d=await json('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate');return {readiness:d.readiness,isPolicyEnabled:d.isPolicyEnabled,commissionRate:d.commissionRate}};
  const rows=[];const config=await json('/api/admin/store-config');
  await json('/api/admin/store-config/status','PUT',{storefrontStatus:'live',rowVersion:config.rowVersion});rows.push(await read());
  await json('/api/admin/payment-methods/COD/enable','POST');rows.push(await read());
  const shipping=await json('/api/shippingmethods/ffd597bb-6276-48ef-bbf7-869a151c2e20');
  await json('/api/shippingmethods/'+shipping.id,'PUT',{...shipping,isActive:true});rows.push(await read());
  if(!rows[2].readiness.isPromotionEligible||rows[0].readiness.isPromotionEligible||rows[1].readiness.isPromotionEligible)throw Error('Partial restoration admission error');
  return rows;
 });
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 await page.getByText('Ready for Affiliate promotion',{exact:true}).waitFor();
 return {scenarios:['G','J'],method:'supported isolated Admin APIs; headed restored table',results};
}
