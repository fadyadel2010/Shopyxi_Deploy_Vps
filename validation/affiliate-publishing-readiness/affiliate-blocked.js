async page => {
 if(!page.url().startsWith('http://affiliate.localhost:18180/'))throw Error('Isolated Affiliate only');
 const pending=page.waitForResponse(r=>r.url().includes('/api/affiliate/discover/products')&&r.request().method()==='GET');
 await page.goto('http://affiliate.localhost:18180/affiliate/portal?tab=discover');
 await pending;
 await page.getByText('QA-AHRAM-AFFILIATE-E2E-20261115',{exact:true}).waitFor({state:'hidden'});
 return await page.evaluate(async()=>{
  const id='37d9a1b6-6585-f655-0e55-824a517ce90c';const results=[];
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  for(const [path,method,body] of [
   ['/api/affiliate/discover/products','GET'],
   ['/api/affiliate/discover/products/'+id,'GET'],
   ['/api/affiliate/promotion-links','GET'],
   ['/api/affiliate/promotion-links/01a1dfa0-8334-755b-aeb8-c037243e70c0','GET'],
   ['/api/affiliate/promotion-links','POST',{tenantId:'15150000-2026-4015-a000-000000000001',productId:id,isPromotionEligible:true}],
   ['/api/affiliate/promotion-links','POST',{tenantId:'00000000-0000-4000-a000-000000000009',productId:id}]
  ]){
   const r=await fetch(path,{method,headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:body?JSON.stringify(body):undefined});const d=await r.json();
   results.push({path,method,crossTenant:body?.tenantId?.endsWith('000009')??false,status:r.status,cacheControl:r.headers.get('cache-control'),count:Array.isArray(d)?d.length:d.items?.length,code:d.code??null});
  }
  if(results[0].count!==0||results[1].status!==404||results[2].count!==0||results[3].status!==404||results[4].code!=='AFFILIATE_PROMOTION_NOT_READY'||results[5].status!==404)throw Error('Readiness bypass');
  return {scenarios:['G','H','I'],results};
 });
}
