"""Native headed login/logout/login. Never echoes credentials or authentication responses."""
import json
import os
from pathlib import Path
import shutil
import subprocess

private = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-acceptance-private'
identity = json.loads((private / 'qa-identity.json').read_text(encoding='utf-8'))
code = r'''async page => {
    const identity = IDENTITY;
    const logins = [];
    let verificationScreen = null;
    if (page.url().split('?')[0].endsWith('/affiliate/verify-email')) {
        verificationScreen = await page.locator('body').innerText();
        await page.screenshot({path:'validation/affiliate-production-acceptance/verification-success.png',fullPage:true});
        await page.locator('a[href="/affiliate/login"]').click();
    }
    const login = async () => {
        await page.locator('#aff-login-email').fill(identity.email);
        await page.locator('#aff-login-password').fill(identity.password);
        const pending = page.waitForResponse(r=>r.url().endsWith('/api/affiliate/auth/login') && r.request().method()==='POST');
        await page.locator('form button[type="submit"]').click();
        const response = await pending;
        const body = await response.json();
        const result = {status:response.status(),code:body.code||null,affiliateAccountId:body.affiliateAccountId,
            emailVerified:body.emailVerified,correlationId:response.headers()['x-correlation-id']||null};
        if (response.ok()) {
            const cookie = (await page.context().cookies()).find(c=>c.name==='affiliate_access_token');
            const claims = await page.evaluate(token => { const b=token.split('.')[1].replace(/-/g,'+').replace(/_/g,'/'); const d=JSON.parse(atob(b)); return {role:d.role||d['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'],subject:d.sub,affiliateAccountId:d.affiliate_account_id,hasTenantClaim:!!d.tenant_id}; }, cookie.value);
            result.claims=claims;
            await page.waitForURL('**/affiliate/portal*');
            await page.locator('nav button[aria-current="page"]').waitFor();
        }
        logins.push(result);
        return response.ok();
    };
    if (page.url().split('?')[0].endsWith('/affiliate/portal')) {
        logins.push({status:200,source:'First native login succeeded; UI portal confirmed after capture interrupted'});
    } else {
        if (!page.url().split('?')[0].endsWith('/affiliate/login')) throw new Error('Normal login page required');
        if (!await login()) return {logins,outcome:'First normal login failed; no retry'};
    }
    const logoutResponse=page.waitForResponse(r=>r.url().endsWith('/api/affiliate/auth/logout')&&r.request().method()==='POST');
    await page.getByRole('button',{name:/^log\s?out$/i}).click();
    const loggedOut=await logoutResponse;
    await page.waitForURL('**/affiliate/login*');
    if (!await login()) return {logins,logoutStatus:loggedOut.status(),outcome:'Second normal login failed'};
    await page.screenshot({path:'validation/affiliate-production-acceptance/authenticated-dashboard.png',fullPage:true,
        mask:[page.getByText(identity.email,{exact:true})]});
    return {verifiedAtUtc:new Date().toISOString(),verificationScreen,logins,logoutStatus:loggedOut.status(),
        route:page.url().split('?')[0].replace('https://affiliate.shopyxi.com',''),outcome:'Native login, logout and second login completed'};
}'''.replace('IDENTITY',json.dumps(identity))
script = private / 'authenticate-browser.js'
script.write_text(code,encoding='utf-8')
result = subprocess.run([shutil.which('playwright-cli.cmd'),'-s=shopixy-affiliate-qa','run-code','--filename',str(script)],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=120)
text=result.stdout.replace('\r\n','\n')
if '### Result\n' not in text:
    error=text.split('### Ran Playwright code',1)[0]
    for value in identity.values(): error=error.replace(value,'[withheld]')
    print(error[:1200]); raise SystemExit('Authentication outcome incomplete; inspect current UI before retry')
safe=json.loads(text.split('### Result\n',1)[1].split('\n### ',1)[0])
Path('validation/affiliate-production-acceptance/authentication-result.json').write_text(json.dumps(safe,indent=2),encoding='utf-8')
print(json.dumps(safe,indent=2))
