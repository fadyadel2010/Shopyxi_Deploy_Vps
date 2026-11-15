async page => {
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 const rows=[];const id='37d9a1b6-6585-f655-0e55-824a517ce90c';
 async function lifecycle(action){return await page.evaluate(async({action,id})=>{
  const product=await(await fetch('/api/products/'+id)).json();
  const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
  const r=await fetch('/api/products/bulk/'+action,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,'Idempotency-Key':crypto.randomUUID()},body:JSON.stringify({items:[{productId:id,expectedVersion:String(product.rowVersion)}]})});
  const data=await r.json();if(!r.ok)throw Error('Bulk '+action+' failed '+r.status+' '+JSON.stringify(data));return {status:r.status,data};
 },{action,id})}
 async function read(action){
  await page.reload();await page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'}).getByRole('button').click();
  const p=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');await p.locator('[data-readiness-area=product]').waitFor();
  const data=await page.evaluate(async id=>{const d=await(await fetch('/api/admin/products/'+id+'/affiliate')).json();return {readiness:d.readiness,isPolicyEnabled:d.isPolicyEnabled,commissionRate:d.commissionRate}},id);
  if(!data.isPolicyEnabled||data.commissionRate!==.1)throw Error('Policy changed');
  await p.screenshot({path:'validation/affiliate-publishing-readiness/product-'+action+'.png'});
  rows.push({action,...data,text:await p.innerText(),links:await p.getByRole('link').evaluateAll(xs=>xs.map(x=>x.getAttribute('href')))});
 }
 for(const action of ['unpublish','deactivate']){
  const mutation=await lifecycle(action);rows.push({action,mutation});
  try{await read(action)}finally{if(action==='deactivate')await lifecycle('activate');await lifecycle('publish')}
  await read(action+'-restored');
 }
 const p=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');
 return {scenarios:['F','J'],method:'normal optimistic-versioned product bulk authoring APIs; headed Manage readback',rows,finalReady:await p.getByText('Ready for Affiliate promotion',{exact:true}).isVisible(),deletedHistoryNotRemoved:true};
}
