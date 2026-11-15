"""Read-only PostgreSQL authority check; no credentials or user rows."""
import json
import subprocess
from pathlib import Path

sql = '''BEGIN READ ONLY;
SELECT json_build_object(
'roles',(SELECT json_agg(x) FROM (SELECT rolname,rolsuper,rolcreaterole,rolcreatedb,rolbypassrls FROM pg_roles WHERE rolname IN ('shopixy_api_runtime','shopixy_api_runtime_dml_v1')) x),
'membership',(SELECT pg_has_role('shopixy_api_runtime','shopixy_api_runtime_dml_v1','MEMBER')),
'permissions',(SELECT json_agg(x) FROM (SELECT t AS table_name,r AS role_name,p AS privilege,has_table_privilege(r,format('public.%I',t),p) AS allowed FROM unnest(ARRAY['AffiliateAccounts','AffiliateUsers','AffiliateEmailVerificationTokens','AffiliatePasswordResetTokens','AffiliateRefreshTokenReplayEntries']) t CROSS JOIN unnest(ARRAY['shopixy_api_runtime','shopixy_api_runtime_dml_v1']) r CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE']) p) x));
ROLLBACK;
'''
command = r'C:\Windows\System32\OpenSSH\ssh.exe'
result = subprocess.run([command, '-o', 'BatchMode=yes', 'shopixy-vps', '/usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'], input=sql, capture_output=True, text=True, timeout=90)
if result.returncode:
    raise SystemExit('Read-only grant inspection failed; raw output withheld')
safe = json.loads(result.stdout.strip())
Path('validation/affiliate-production-acceptance/auth-grants-before.json').write_text(json.dumps(safe, indent=2), encoding='utf-8')
print(json.dumps(safe, indent=2))
