"""Apply tested column-only classification ACLs. No business writes or workload rollout."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

root=Path(__file__).resolve().parents[2]
out=Path(__file__).resolve().parent
started=out/'classification-grant-started.json'
if started.exists(): raise SystemExit('Existing attempt; inspect state, no automatic retry')
log=(out/'grant-regression.log').read_bytes()
assert '"classificationPassed":true' in log.decode('utf-16' if log.startswith(b'\xff\xfe') else 'utf-8-sig')
sql=(root/'deploy/affiliate/03-commission-classification-read-grant.sql').read_text(encoding='utf-8')
check='''BEGIN READ ONLY;
SELECT json_agg(x) FROM (SELECT c.relname AS name,
has_column_privilege('shopixy_api_runtime',c.oid,'OrderId','SELECT') AS can_read_order_id,
has_table_privilege('shopixy_api_runtime',c.oid,'SELECT') AS can_read_whole_table,
has_table_privilege('shopixy_api_runtime',c.oid,'INSERT') AS can_insert,
has_table_privilege('shopixy_api_runtime',c.oid,'UPDATE') AS can_update,
has_table_privilege('shopixy_api_runtime',c.oid,'DELETE') AS can_delete
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
AND c.relname IN ('SyntheticQaOrders','SyntheticQaEvidence','SyntheticQaCommissionPostings') ORDER BY c.relname) x;
ROLLBACK;'''
def remote(statement):
    p=subprocess.run([r'C:\Windows\System32\OpenSSH\ssh.exe','-o','BatchMode=yes','shopixy-vps',
        '/usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],
        input=statement,capture_output=True,text=True,timeout=90)
    if p.returncode: raise RuntimeError('Bounded ACL operation failed; no raw output exported')
    return p.stdout
before=json.loads(remote(check).strip())
assert len(before)==3 and all(r['can_read_order_id'] == (r['name']=='SyntheticQaOrders') and
    not any(v for k,v in r.items() if k not in ('name','can_read_order_id')) for r in before)
receipt={'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'sqlSha256':hashlib.sha256(sql.encode()).hexdigest(),
    'before':before,'scope':'SELECT(OrderId) only on SyntheticQaEvidence and SyntheticQaCommissionPostings; existing SyntheticQaOrders grant preserved; no writes or role changes'}
started.write_text(json.dumps(receipt,indent=2),encoding='utf-8')
remote(sql)
after=json.loads(remote(check).strip())
assert len(after)==3 and all(r['can_read_order_id'] and not any(v for k,v in r.items() if k not in ('name','can_read_order_id')) for r in after)
receipt.update({'completedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'after':after,'passed':True,
    'businessRowsWritten':False,'applicationImagesChanged':False})
(out/'classification-grant-deployment.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
print(json.dumps(receipt,indent=2))
