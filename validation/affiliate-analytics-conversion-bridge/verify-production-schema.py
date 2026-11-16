"""Read-only Production schema admission/verification; emits booleans only."""
import datetime,json
from pathlib import Path
import subprocess,sys
out=Path(__file__).parent;stage=sys.argv[1];assert stage in ('preflight','after')
sql=(out/('production-schema-preflight.sql' if stage=='preflight' else 'production-schema-verify.sql')).read_text()
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo /usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],input=sql,text=True,capture_output=True,timeout=120)
if r.returncode: raise SystemExit('Read-only schema verification failed; raw output withheld')
checks=r.stdout.strip().splitlines();assert checks and all(x=='t' for x in checks)
data={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stage':stage,
 'readOnly':True,'checksPassed':len(checks),'businessRowsChanged':False}
target=out/('production-schema-'+stage+'.json');assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
