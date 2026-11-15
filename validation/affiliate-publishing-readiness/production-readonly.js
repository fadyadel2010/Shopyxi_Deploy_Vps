async page => {
 let origin=page.url().split('/').slice(0,3).join('/');
 if(origin==='about:blank'){
  const cookies=await page.context().cookies();
  if(!cookies.some(c=>c.domain==='admin.shopyxi.com'&&/auth|token|session/i.test(c.name)))throw Error('Existing Production Owner context unavailable');
  origin='https://admin.shopyxi.com';
 }
 if(!origin.startsWith('https://')||origin.includes('localhost'))throw Error('Expected established Production HTTPS Owner context');
 const blocked=[];let authRefreshes=0;
 const guard=async route=>{
  const request=route.request();const method=request.method();const path=request.url().split('?')[0].replace(/^https?:\/\/[^/]+/,'');
  if(!['GET','HEAD','OPTIONS'].includes(method)){
   if(method==='POST'&&path==='/api/auth/refresh'){authRefreshes++;return route.continue()}
   blocked.push({method,path});return route.abort('blockedbyclient');
  }
  return route.continue();
 };
  await page.route('**/api/**',guard);
  try{
   const listResponse=page.waitForResponse(r=>r.request().method()==='GET'&&r.url().split('?')[0].endsWith('/api/admin/affiliate/products')&&r.status()===200,{timeout:60000});
   await page.goto(origin+'/admin/affiliates/products');
   const response=await listResponse;const products=await response.json();
   if(!Array.isArray(products))throw Error('Expected authenticated bounded product list');
   await page.locator('table[aria-busy=false]').waitFor();
   const rows=page.locator('tbody tr').filter({has:page.getByTestId('affiliate-promotion-readiness')});
   if(products.length>0)await rows.first().waitFor();
   const count=await rows.count();
   if(count!==products.length)throw Error('Rendered rows differ from loaded server response');
   const completeReadiness=products.every(p=>p.readiness&&['storefrontReady','paymentReady','shippingReady','productSellable','isPromotionEligible'].every(k=>typeof p.readiness[k]==='boolean')&&Array.isArray(p.readiness.blockerCodes));
   if(!completeReadiness)throw Error('Incomplete server-derived readiness');
  let sections=0;let blockerCodes=[];
  if(count>0){
   await rows.first().getByRole('button').click();
   const panel=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');
   await panel.locator('[data-readiness-area=product]').waitFor();
   sections=await panel.locator('[data-readiness-area]').count();
   blockerCodes=await panel.locator('[data-blocker-code]').evaluateAll(xs=>xs.map(x=>x.getAttribute('data-blocker-code')));
   if(sections!==4)throw Error('Readiness panel incomplete');
  }
   return {verifiedAtUtc:new Date().toISOString(),origin,path:'/admin/affiliates/products',headedSessionReused:true,authenticatedOwnerSurface:true,
    listHttpStatus:response.status(),serverRowsLoaded:products.length,serverReadinessShapeVerified:completeReadiness,
   existingProductRows:count,serverDerivedEligibilityVisible:count>0,fourSectionPanelVerified:sections===4,
   blockerCodes,businessMutationRequestsSent:0,blockedMutationRequests:blocked,authenticationOnlyRefreshRequests:authRefreshes,
   screenshotSaved:false,privateProductIdentityOrCommissionExported:false};
 }finally{await page.unroute('**/api/**',guard)}
}
