"""Reuse the preceding read-only isolation predicate without overwriting its receipts."""
import ast
import datetime
import json
from pathlib import Path
import subprocess
import sys

out = Path(__file__).parent
stage = sys.argv[1];assert stage in ('before','after')
tree = ast.parse((out.parent/'affiliate-full-e2e/verify-production-boundary.py').read_text())
sql = next(ast.literal_eval(node.value) for node in tree.body if isinstance(node,ast.Assign) and any(isinstance(t,ast.Name) and t.id == 'sql' for t in node.targets))
result = subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo /usr/local/bin/kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app'],input=sql,text=True,capture_output=True,timeout=90)
assert result.returncode == 0,'Read-only boundary verification failed'
counts = json.loads(result.stdout.strip()); assert all(value == 0 for value in counts.values())
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stage':stage,'readOnlyRepeatableRead':True,'businessDataWrites':False,'fixtureCounts':counts,'passed':True}
(out/('production-boundary-'+stage+'.json')).write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
