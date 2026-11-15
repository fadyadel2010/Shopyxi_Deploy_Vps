async page => {
 const product='37d9a1b6-6585-f655-0e55-824a517ce90c'; const results=[];
 const beforeUnload=dialog=>{if(dialog.type()==='beforeunload')return dialog.accept()};
 page.on('dialog',beforeUnload);
 if(!page.url().startsWith('http://admin.localhost:18180/')) throw Error('Isolated Owner only');
 async function setStatus(value){
  await page.evaluate(async value=>{
   const current=await(await fetch('/api/admin/store-config')).json();
   if(current.storefrontStatus===value)return;
   const csrf=decodeURIComponent(document.cookie.split('; ').find(x=>x.startsWith('csrf_token='))?.slice(11)??'');
   const r=await fetch('/api/admin/store-config/status',{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:JSON.stringify({storefrontStatus:value,rowVersion:current.rowVersion})});
   if(r.status!==200)throw Error('Status mutation failed '+r.status);
  },value);
 }
 async function read(value){
  await page.goto('http://admin.localhost:18180/admin/affiliates/products');
  const row=page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'});
  const diagnostic=await page.evaluate(async()=>{const r=await fetch('/api/admin/affiliate/products');const d=await r.json();return {status:r.status,items:Array.isArray(d)?d.length:null,keys:Object.keys(d)}});
  if(diagnostic.status!==200||diagnostic.items!==1){results.push({value,diagnostic});return;}
  await row.waitFor();
  await row.getByRole('button',{name:'Manage: QA-AHRAM-AFFILIATE-E2E-20261115',exact:true}).click();
  const panel=page.getByRole('dialog').getByTestId('affiliate-promotion-readiness');
  await panel.locator('[data-readiness-area=storefront]').waitFor();
  const data=await page.evaluate(async id=>{const r=await fetch('/api/admin/products/'+id+'/affiliate');return await r.json()},product);
  if(data.readiness.isPromotionEligible!==(value==='live'))throw Error('Incorrect status eligibility');
  if(!data.isPolicyEnabled||data.commissionRate!==.1)throw Error('Policy changed');
  await panel.screenshot({path:'validation/affiliate-publishing-readiness/status-'+value+'.png'});
  results.push({value,readiness:data.readiness,policyEnabled:data.isPolicyEnabled,commissionRate:data.commissionRate,
    panel:await panel.innerText(),links:await panel.getByRole('link').evaluateAll(xs=>xs.map(a=>a.getAttribute('href')))});
 }
 for(const status of ['disabled','maintenance']){
  await setStatus(status);
  try{await read(status)}finally{await setStatus('live')}
  await read('live');
 }
 page.off('dialog',beforeUnload);
 return {scenarios:['B','C','J'],method:'existing optimistic-concurrency Admin status API fixture operations, headed Manage readback',results};
}
