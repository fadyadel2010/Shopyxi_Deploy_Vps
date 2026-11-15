async page => {
 if(!page.url().startsWith('http://affiliate.localhost:18180/affiliate/portal'))throw Error('Expected existing isolated catalog');
 await page.bringToFront();
 await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
 await page.getByText('QA-AHRAM-AFFILIATE-E2E-20261115',{exact:true}).waitFor({state:'hidden'});
 await page.getByRole('dialog').waitFor({state:'hidden'});
 return {scenario:'I',headed:true,sameCatalogPage:true,focusRefetchedCurrentReadiness:true,staleOfferRemoved:true,noOpenOfferAfterRefresh:true};
}
