async page => {
 await page.goto('http://ahram.localhost:18181/products/qa-ahram-affiliate-e2e-20261115?ref=WWC3NC7R9B');
 const result=await page.evaluate(async()=>{
  const r=await fetch('/api/storefront/products/by-id/37d9a1b6-6585-f655-0e55-824a517ce90c?ref=WWC3NC7R9B');
  const landing=await fetch('/api/storefront/landing/WWC3NC7R9B/qa-ahram-affiliate-e2e-20261115');
  return {ordinaryProductStatus:r.status,referralAccepted:!!r.headers.get('X-Affiliate-Ref'),affiliateLandingStatus:landing.status};
 });
 if(result.referralAccepted||result.affiliateLandingStatus!==404)throw Error('Ineligible referral accepted');
 return {scenario:'I',result,checkoutSubmitted:false};
}
