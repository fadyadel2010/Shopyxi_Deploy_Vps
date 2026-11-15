async page => {
  const consoleTypes=[]; const onConsole=m=>{if(m.type()==='error')consoleTypes.push('error')};
  page.on('console',onConsole);
  await page.goto('http://admin.localhost:18180/admin/affiliates/products');
  const row=page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'});
  await row.getByText('Ready for Affiliate promotion',{exact:true}).waitFor();
   await row.getByRole('button',{name:'Manage: QA-AHRAM-AFFILIATE-E2E-20261115',exact:true}).click();
  const dialog=page.getByRole('dialog');
  await dialog.getByText('Ready for Affiliate promotion',{exact:true}).waitFor();
  const before=await page.evaluate(async()=>{
    const r=await fetch('/api/admin/products/37d9a1b6-6585-f655-0e55-824a517ce90c/affiliate');
    const d=await r.json();return {status:r.status,isPolicyEnabled:d.isPolicyEnabled,readiness:d.readiness};
  });
  const buttons=await dialog.getByRole('button').allTextContents();
  await dialog.screenshot({path:'validation/affiliate-publishing-readiness/scenario-A-manage.png'});
  page.off('console',onConsole);
  return {scenario:'A',before,buttons,console:{errorCount:consoleTypes.length,rawMessagesOmitted:true}};
}
