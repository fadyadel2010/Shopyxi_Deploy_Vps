async page => {
 const url=page.url().split('?')[0];
 const cookies=await page.context().cookies();
 return {origin:url.split('/').slice(0,3).join('/'),headedSessionReused:true,
  tabs:page.context().pages().map((p,index)=>({index,origin:p.url().split('/').slice(0,3).join('/')})),
  hasExistingAuthenticationCookies:cookies.some(c=>/auth|token|session/i.test(c.name)),
  authenticationCookieDomains:[...new Set(cookies.filter(c=>/auth|token|session/i.test(c.name)).map(c=>c.domain))],
  cookieValuesExported:false};
}
