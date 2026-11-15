async page => {
 if(!page.url().startsWith('http://admin.localhost:18180/'))throw Error('Isolated Owner only');
 return await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const path='/api/shippingmethods/ffd597bb-6276-48ef-bbf7-869a151c2e20';const m=await(await fetch(path)).json();
  const r=await fetch(path,{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,'If-Match':'"'+m.revision+'"'},body:JSON.stringify({...m,isActive:true})});
  const d=await(await fetch('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate')).json();
  if(r.status!==204||!d.readiness.isPromotionEligible||!d.isPolicyEnabled||d.commissionRate!==.1)throw Error('Shipping restoration failed');
  return {scenarios:['E','J'],status:r.status,readiness:d.readiness,isPolicyEnabled:d.isPolicyEnabled,commissionRate:d.commissionRate,manualShippingQualifiesWithoutCarrierMapping:true};
 });
}
