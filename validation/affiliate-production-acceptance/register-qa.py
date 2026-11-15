"""One deliberate UI enrollment. Credential material stays in the approved private temp directory.

Never auto-retry this script. If the browser result is uncertain, inspect the account first.
The operator supplies the approved mailbox interactively; it is not stored in this repository.
"""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys

private = Path(os.environ["LOCALAPPDATA"]) / "Temp/opencode/shopixy-affiliate-acceptance-private"
private.mkdir(exist_ok=True)
credentials = private / "qa-identity.json"
if credentials.exists():
    if sys.argv[1:] not in (["--confirmed-no-request"], ["--confirmed-rolled-back"]):
        raise SystemExit("Private identity reference already exists. Do not register again.")
    if sys.argv[1:] == ["--confirmed-rolled-back"]:
        diagnostic = json.loads(Path('validation/affiliate-production-acceptance/registration-diagnostic.json').read_text())
        assert diagnostic['state']['identity'] == [], 'Account exists; do not enroll again'
    # Explicit operator recovery only after browser traffic proves no registration POST occurred.
else:
    mailbox = sys.argv[1]
    password = "Q!a9" + secrets.token_urlsafe(28)
    credentials.write_text(json.dumps({"email": mailbox, "password": password, "displayName": "QA-AFFILIATE-E2E-20261115"}), encoding="utf-8")
code = r'''async page => {
    const identity = IDENTITY;
    if (!page.url().split('?')[0].endsWith('/affiliate/register')) throw new Error('Registration page required');
    await page.getByRole('heading', {name:'Join as Affiliate',exact:true}).waitFor();
    await page.locator('#aff-reg-name').fill(identity.displayName);
    await page.locator('#aff-reg-email').fill(identity.email);
    await page.locator('#aff-reg-password').fill(identity.password);
    await page.locator('#aff-reg-confirm').fill(identity.password);
    const responsePromise = page.waitForResponse(r => r.url().endsWith('/api/affiliate/auth/register') && r.request().method() === 'POST');
    await page.getByRole('button',{name:'Create Affiliate Account',exact:true}).click();
    const response = await responsePromise;
    const body = await response.json();
    const result = {status:response.status(), affiliateAccountId:body.affiliateAccountId || null,
        referralCode:body.referralCode || null, displayName:body.displayName || null,
        emailVerified:body.emailVerified ?? null, code:body.code || null, accountCreated:body.accountCreated ?? (response.status()===200 ? true : null),
        verificationSubmittedOnce:true, correlationId:response.headers()['x-correlation-id'] || null};
    if (response.status()===200) await page.waitForURL('**/affiliate/login?registered=1*');
    result.route = page.url().split('?')[0].replace('https://affiliate.shopyxi.com','');
    await page.screenshot({path:'validation/affiliate-production-acceptance/registration-result.png',fullPage:true,
        mask:[page.locator('input[type="email"]'),page.locator('input[type="password"]')]});
    return result;
}'''.replace("IDENTITY", json.dumps(json.loads(credentials.read_text(encoding="utf-8"))))
cli = shutil.which("playwright-cli.cmd")
private_script = private / "register-browser.js"
private_script.write_text(code, encoding="utf-8")
result = subprocess.run([cli, "-s=shopixy-affiliate-qa", "run-code", "--filename", str(private_script)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90)
# The CLI echoes executed source, which includes credentials. Never print or persist that output.
marker = "### Result\n"
text = result.stdout.replace("\r\n", "\n")
if marker not in text:
    error = text.split('### Ran Playwright code', 1)[0]
    for value in json.loads(credentials.read_text(encoding='utf-8')).values():
        error = error.replace(value, '[WITHHELD]')
    print(error[:2000] or 'No browser stdout; process exit=' + str(result.returncode))
    raise SystemExit("Browser outcome uncertain. Private identity retained. Inspect state; DO NOT retry registration.")
safe = json.loads(text.split(marker, 1)[1].split("\n### ", 1)[0])
safe["mailbox"] = "operator-approved QA verification mailbox (withheld)"
Path("validation/affiliate-production-acceptance/registration-result.json").write_text(json.dumps(safe, indent=2), encoding="utf-8")
print(json.dumps(safe, indent=2))
