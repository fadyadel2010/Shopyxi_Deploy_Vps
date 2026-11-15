async page => {
 if(!page.url().startsWith('http://admin.localhost:18180/'))throw Error('Isolated Owner only');
 const result=await page.evaluate(async()=>{
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const r=await fetch('/api/admin/payment-methods/COD/enable',{method:'POST',headers:{'X-CSRF-Token':csrf}});
  const d=await(await fetch('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate')).json();
  if(r.status!==200||!d.readiness.isPromotionEligible||!d.isPolicyEnabled||d.commissionRate!==.1)throw Error('COD restoration failed');
  return {status:r.status,readiness:d.readiness,isPolicyEnabled:d.isPolicyEnabled,commissionRate:d.commissionRate};
 });return {scenarios:['D','J'],result};
}
