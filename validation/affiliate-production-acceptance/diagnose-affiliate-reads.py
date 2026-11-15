"""Read-only metadata for failed Affiliate portal SQL reads. No raw SQL or row data exported."""
import json
from pathlib import Path
import subprocess

remote = r'''
import datetime,json,re,subprocess
K=['/usr/local/bin/kubectl']
p=subprocess.run(K+['logs','shopixy-postgres-1','-n','shopixy-data','-c','postgres','--since=20m','--tail=6000'],capture_output=True,text=True,timeout=60,check=True)
errors=[]
for line in p.stdout.splitlines():
    try: record=json.loads(line).get('record',{})
    except Exception: continue
    query=record.get('query','')
    if record.get('error_severity') != 'ERROR' or 'Affiliate' not in query: continue
    message=record.get('message','')
    if not re.fullmatch(r'(?:permission denied for table [A-Za-z_0-9]+|column [A-Za-z_."0-9]+ does not exist|relation "[A-Za-z_0-9]+" does not exist)',message): continue
    errors.append({'atUtc':record.get('log_time'),'sqlState':record.get('sql_state_code'),'message':message})
print(json.dumps({'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'errors':errors},indent=2))
'''
p = subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe','-o','BatchMode=yes','shopixy-vps','python3','-'],input=remote,capture_output=True,text=True,timeout=90)
if p.returncode: raise SystemExit('Read diagnostic failed; no raw logs exported')
safe=json.loads(p.stdout)
Path('validation/affiliate-production-acceptance/portal-read-diagnostic.json').write_text(json.dumps(safe,indent=2),encoding='utf-8')
print(json.dumps(safe,indent=2))
