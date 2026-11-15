async page => {
 if(!page.url().startsWith('http://affiliate.localhost:18180/affiliate/portal'))throw Error('Expected existing isolated catalog');
 await page.bringToFront();await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
 await page.getByText('QA-AHRAM-AFFILIATE-E2E-20261115',{exact:true}).waitFor();
 return {scenario:'J',sameCatalogPage:true,offerRestoredWithoutNavigation:true};
}
