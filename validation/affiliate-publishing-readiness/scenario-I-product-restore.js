async page => {
 if(!page.url().startsWith('http://admin.localhost:18180/'))throw Error('Isolated Owner only');
 return await page.evaluate(async()=>{
  const id='37d9a1b6-6585-f655-0e55-824a517ce90c';
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  for(const action of ['activate','publish']){
   const p=await(await fetch('/api/products/'+id)).json();
   const r=await fetch('/api/products/bulk/'+action,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,'Idempotency-Key':crypto.randomUUID()},body:JSON.stringify({items:[{productId:id,expectedVersion:String(p.rowVersion)}]})});
   const d=await r.json();if(!r.ok||d.failed)throw Error('Product restoration failed');
  }
  const d=await(await fetch('/api/admin/products/'+id+'/affiliate')).json();
  if(!d.readiness.isPromotionEligible||!d.isPolicyEnabled||d.commissionRate!==.1)throw Error('Eligibility not restored');
  return {scenarios:['I','J'],readiness:d.readiness,isPolicyEnabled:d.isPolicyEnabled,commissionRate:d.commissionRate};
 });
}
