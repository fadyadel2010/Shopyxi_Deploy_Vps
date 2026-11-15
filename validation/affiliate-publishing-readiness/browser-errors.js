async page => {
 const errors=[],resources=[];
 const onConsole=m=>{if(m.type()==='error'){const text=m.text();errors.push({kind:text.includes('Content Security Policy')?'csp':text.includes('Failed to load resource')?'resource':text.includes('WebSocket')?'websocket':'other',status404:text.includes('404')})}};
 const onResponse=r=>{if(r.status()>=400)resources.push({path:r.url().split('?')[0].replace(/^https?:\/\/[^/]+/,''),status:r.status()})};
 let javascriptErrors=0;const onError=()=>{javascriptErrors++};
 page.on('console',onConsole);page.on('response',onResponse);page.on('pageerror',onError);
 await page.goto('http://admin.localhost:18180/admin/affiliates/products');
 await page.getByRole('row').filter({hasText:'QA-AHRAM-AFFILIATE-E2E-20261115'}).getByRole('button').click();
 await page.getByRole('dialog').getByTestId('affiliate-promotion-readiness').locator('[data-readiness-area=product]').waitFor();
 page.off('console',onConsole);page.off('response',onResponse);page.off('pageerror',onError);
 return {javascriptErrors,errors,resources,privateMessagesOmitted:true};
}
