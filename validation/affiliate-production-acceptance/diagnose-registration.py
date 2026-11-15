"""Read-only, exact QA identity / correlated error diagnostics. No raw logs or credentials exported."""
import json
import os
from pathlib import Path
import subprocess

private = Path(os.environ["LOCALAPPDATA"]) / "Temp/opencode/shopixy-affiliate-acceptance-private"
identity = json.loads((private / "qa-identity.json").read_text(encoding="utf-8"))
remote = r'''
import datetime,json,re,subprocess
K=['/usr/local/bin/kubectl']
def run(args, data=None):
    p=subprocess.run(args,input=data,capture_output=True,text=True,timeout=60)
    if p.returncode:
        errors=re.findall(r'(?:ERROR:|error:)[^\n]*',p.stderr)
        safe=re.sub(r'[A-Za-z0-9_.+-]+@[A-Za-z0-9.-]+','[mailbox withheld]',errors[0] if errors else 'raw output withheld')
        raise RuntimeError('Read-only diagnostic command failed: '+safe[:300])
    return p.stdout
logs=run(K+['logs','deployment/shopixy-api','-n','shopixy-app','-c','api','--since=60m','--tail=10000'])
lines=logs.splitlines()
events=[]
safeApplicationErrors=[]
for line in lines:
    try: entry=json.loads(line)
    except Exception: continue
    if entry.get('Category') == 'PointOfSale.API.Middleware.GlobalExceptionMiddleware' and entry.get('LogLevel') == 'Error':
        safeApplicationErrors.append({k:entry.get(k) for k in ('Timestamp','ExceptionCategory','OperationalFields')})
for i,line in enumerate(lines):
    if '/api/affiliate/auth/register' in line and ('Dependency failure' in line or 'Unhandled exception' in line):
        section='\n'.join(lines[i:i+70])
        events.append({'correlationIds':re.findall(r'CorrelationId[:=]\s*([0-9a-f-]{36})',line),
            'exceptionTypes':sorted(set(re.findall(r'\b((?:Npgsql|Microsoft\.[A-Za-z.]+|System\.[A-Za-z.]+)\w*Exception)',section))),
            'sqlStates':sorted(set(re.findall(r'\b(?:SqlState:\s*|PostgresException[^\n]*?\b)([0-9A-Z]{5})\b',section))),
            'missingColumns':sorted(set(re.findall(r'column ([A-Za-z_."0-9]+) does not exist',section))),
            'missingRelations':sorted(set(re.findall(r'relation "([A-Za-z_0-9]+)" does not exist',section))),
            'affiliateStackMethods':sorted(set(re.findall(r'PointOfSale\.API\.Controllers\.AffiliateAuthController\.([A-Za-z0-9_]+)',section)))})
email=MAILBOX
postgresErrors=[]
pglogs=run(K+['logs','shopixy-postgres-1','-n','shopixy-data','-c','postgres','--since=60m','--tail=10000'])
for line in pglogs.splitlines():
    try: entry=json.loads(line)
    except Exception: continue
    record=entry.get('record',{})
    query=record.get('query','')
    if ('"AffiliateUsers"' in query or '"AffiliateAccounts"' in query or '"AffiliateEmailVerificationTokens"' in query) and record.get('error_severity') in ('ERROR','FATAL'):
        message=record.get('message','')
        if email.lower() in message.lower(): message='Message contained QA mailbox and was withheld'
        postgresErrors.append({k:record.get(k) for k in ('log_time','sql_state_code','constraint_name','table_name','column_name') } | {'message':message})
sql="""BEGIN READ ONLY;
SELECT json_build_object('schema',coalesce((SELECT json_agg(x) FROM (SELECT table_name,column_name,data_type FROM information_schema.columns WHERE table_schema='public' AND table_name IN ('AffiliateUsers','AffiliateAccounts','AffiliateEmailVerificationTokens') ORDER BY table_name,ordinal_position) x),'[]'::json),
'identity',coalesce((SELECT json_agg(x) FROM (SELECT u.\"Id\" AS \"affiliateUserId\",u.\"AffiliateAccountId\" AS \"affiliateAccountId\",u.\"EmailVerified\" AS \"emailVerified\",u.\"IsDeleted\" AS \"isDeleted\",u.\"CreatedAt\" AS \"createdAt\",a.\"DisplayName\" AS \"displayName\",a.\"Status\" AS \"status\" FROM \"AffiliateUsers\" u LEFT JOIN \"AffiliateAccounts\" a ON a.\"Id\"=u.\"AffiliateAccountId\" WHERE u.\"Email\"='"""+email.replace("'","''")+"' LIMIT 2) x),'[]'::json), 'constraints', (SELECT json_agg(x) FROM (SELECT conname,pg_get_constraintdef(oid) AS definition FROM pg_constraint WHERE conrelid IN ('\"AffiliateUsers\"'::regclass,'\"AffiliateAccounts\"'::regclass,'\"AffiliateEmailVerificationTokens\"'::regclass)) x)); ROLLBACK;"
rows=run(K+['exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','postgres','-d','app'],sql)
state=json.loads(rows.strip())
print(json.dumps({'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'method':'Bounded log metadata and BEGIN READ ONLY exact approved QA identity query; no raw logs, mailbox, hashes or tokens exported','registrationErrors':events,'safeApplicationErrors':safeApplicationErrors,'postgresAffiliateErrors':postgresErrors,'state':state},indent=2))
'''.replace("MAILBOX", repr(identity["email"]))
compile(remote, 'readonly-registration-diagnostic', 'exec')
result = subprocess.run([r"C:\Windows\System32\OpenSSH\ssh.exe", "-o", "BatchMode=yes", "shopixy-vps", "python3", "-"], input=remote, capture_output=True, text=True, timeout=180)
if result.returncode:
    error = result.stderr
    for value in identity.values():
        error = error.replace(value, '[withheld]')
    print('Diagnostic exit=' + str(result.returncode) + '; ' + error[-1500:])
    raise SystemExit("Read-only diagnostic failed; raw output withheld")
safe = json.loads(result.stdout)
Path("validation/affiliate-production-acceptance/registration-diagnostic.json").write_text(json.dumps(safe, indent=2), encoding="utf-8")
print(json.dumps(safe, indent=2))
