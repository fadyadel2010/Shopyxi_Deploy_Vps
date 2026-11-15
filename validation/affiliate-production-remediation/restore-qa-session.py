"""Restore only the existing normal QA Affiliate through its native login form. Never print credentials."""
import json
import os
from pathlib import Path
import shutil
import subprocess

private = Path(os.environ['LOCALAPPDATA']) / 'Temp/opencode/shopixy-affiliate-acceptance-private'
identity = json.loads((private/'qa-identity.json').read_text())
code = r'''async page => {
    const identity = IDENTITY;
    if(!page.url().split('?')[0].endsWith('/affiliate/portal')) {
        await page.locator('#aff-login-email').fill(identity.email);
        await page.locator('#aff-login-password').fill(identity.password);
        const pending=page.waitForResponse(r=>r.url().endsWith('/api/affiliate/auth/login')&&r.request().method()==='POST');
        await page.locator('form button[type="submit"]').click();
        const r=await pending;
        if(r.status()!==200) return {loginStatus:r.status(),restored:false};
        await page.waitForURL('**/affiliate/portal*');
    }
    const profile=await page.evaluate(async()=>{const r=await fetch('/api/affiliate/profile',{credentials:'include',cache:'no-store'});const d=await r.json();return {status:r.status,accountId:d.affiliateAccountId};});
    return {verifiedAtUtc:new Date().toISOString(),restored:profile.status===200,profile,newIdentityCreated:false,verificationEmailRequested:false};
}'''.replace('IDENTITY',json.dumps(identity))
script = private/'restore-phase-two-browser.js'; script.write_text(code,encoding='utf-8',newline='\n')
result = subprocess.run([shutil.which('playwright-cli.cmd'),'-s=shopixy-affiliate-qa','run-code','--filename',str(script)],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=120)
text=result.stdout.replace('\r\n','\n')
assert '### Result\n' in text, 'Native QA login could not be confirmed; credential-bearing CLI echo withheld'
safe=json.loads(text.split('### Result\n',1)[1].split('\n### ',1)[0])
assert safe.get('restored') and safe['profile']['accountId']=='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'
(Path(__file__).parent/'qa-session-restored.json').write_text(json.dumps(safe,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(safe,indent=2))
