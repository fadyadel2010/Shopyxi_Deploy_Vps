"""Apply only the independently rehearsed verification-token ACL correction. No business writes."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
out = Path(__file__).resolve().parent
started = out / 'grant-apply-started-v2.json'
if started.exists():
    raise SystemExit('Existing deployment attempt. Inspect ACL and receipt; no automatic retry.')
regression = (out / 'grant-regression.log').read_bytes()
assert '"passed":true' in regression.decode('utf-16' if regression.startswith(b'\xff\xfe') else 'utf-8-sig')
sql = (root / 'deploy/affiliate/01-verification-token-runtime-grant.sql').read_text(encoding='utf-8')
check = '''BEGIN READ ONLY;
SELECT json_build_object('tokenRows',(SELECT count(*) FROM "AffiliateEmailVerificationTokens"),
'canDeleteToken',has_table_privilege('shopixy_api_runtime','public."AffiliateEmailVerificationTokens"','DELETE'),
'canTruncateToken',has_table_privilege('shopixy_api_runtime','public."AffiliateEmailVerificationTokens"','TRUNCATE'),
'canDeleteUser',has_table_privilege('shopixy_api_runtime','public."AffiliateUsers"','DELETE'),
'canDeleteAccount',has_table_privilege('shopixy_api_runtime','public."AffiliateAccounts"','DELETE'),
'roles',(SELECT json_agg(x) FROM (SELECT rolname,rolsuper,rolcreaterole,rolcreatedb,rolbypassrls FROM pg_roles WHERE rolname IN ('shopixy_api_runtime','shopixy_api_runtime_dml_v1') ORDER BY rolname) x)); ROLLBACK;
'''


def remote(statement):
    result = subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe', '-o', 'BatchMode=yes', 'shopixy-vps',
        '/usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
        input=statement, capture_output=True, text=True, timeout=90)
    if result.returncode:
        raise RuntimeError('ACL operation failed; raw output withheld. Inspect exact permissions before any retry.')
    return result.stdout


before = json.loads(remote(check).strip())
assert not any(before[k] for k in ('canDeleteToken','canTruncateToken','canDeleteUser','canDeleteAccount'))
receipt = {'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'sqlSha256':hashlib.sha256(sql.encode()).hexdigest(), 'before':before,
    'scope':'One existing runtime DML role; DELETE only on ephemeral AffiliateEmailVerificationTokens. No business data/role/member/default grants.'}
started.write_text(json.dumps(receipt, indent=2), encoding='utf-8')
remote(sql)
after = json.loads(remote(check).strip())
assert after['canDeleteToken']
assert not any(after[k] for k in ('canTruncateToken','canDeleteUser','canDeleteAccount'))
assert before['roles'] == after['roles']
assert before['tokenRows'] == after['tokenRows']
receipt.update({'completedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(), 'after':after,
    'passed':True,'businessRowsWritten':False,'applicationImagesChanged':False})
(out / 'grant-deployment.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
print(json.dumps(receipt, indent=2))
