async page => {
 if(!page.url().startsWith('http://affiliate.localhost:18180/'))throw Error('Isolated Affiliate only');
 await page.goto('http://affiliate.localhost:18180/affiliate/portal?tab=discover');
 await page.getByText('QA-AHRAM-AFFILIATE-E2E-20261115',{exact:true}).waitFor();
 return await page.evaluate(async()=>{
  const id='37d9a1b6-6585-f655-0e55-824a517ce90c';const r=await fetch('/api/affiliate/discover/products');const d=await r.json();
  const detail=await fetch('/api/affiliate/discover/products/'+id);
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const link=await fetch('/api/affiliate/promotion-links',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:JSON.stringify({tenantId:'15150000-2026-4015-a000-000000000001',productId:id})});
  const data=await link.json();
  if(!d.items.some(x=>x.productId===id)||detail.status!==200||data.id!=='01a1dfa0-8334-755b-aeb8-c037243e70c0')throw Error('Offer did not restore idempotently');
  return {scenarios:['A','J'],catalogStatus:r.status,catalogCount:d.items.length,detailStatus:detail.status,existingLinkStatus:link.status,sameHistoricalLink:true,referralCodeUnchanged:data.referralCode==='WWC3NC7R9B'};
 });
}
