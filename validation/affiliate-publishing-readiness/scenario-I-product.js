async page => {
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 await page.evaluate(async()=>{
  const id='37d9a1b6-6585-f655-0e55-824a517ce90c';const p=await(await fetch('/api/products/'+id)).json();
  if(!p.isActive||p.publishStatus!=='Published')throw Error('Unexpected fixture');
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const r=await fetch('/api/products/bulk/deactivate',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,'Idempotency-Key':crypto.randomUUID()},body:JSON.stringify({items:[{productId:id,expectedVersion:String(p.rowVersion)}]})});
  const d=await r.json();if(!r.ok||d.failed||d.processed!==1)throw Error('Product archive rejected');
 });
 await page.reload();await page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'}).getByRole('button').click();
 const panel=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');await panel.locator('[data-blocker-code=PRODUCT_ARCHIVED]').waitFor();
 const text=await panel.innerText();
 await panel.getByRole('link',{name:'Manage product'}).first().click();
 await page.waitForURL('http://admin.localhost:18180/products/37d9a1b6-6585-f655-0e55-824a517ce90c/edit');
 return {scenario:'I',action:'isolated product archival',text,correctiveProductNavigation:page.url()};
}
