"""Use established headed sessions in place. No authentication-state export or sharing."""
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys

out = Path(__file__).parent
session, name = sys.argv[1:3]
label = sys.argv[3] if len(sys.argv) == 4 else name
assert session in ('affiliate-full-affiliate','affiliate-full-owner','affiliate-full-customer','shopixy-affiliate-public-review','shopixy-r10-owner','shopixy-r10-cashier','shopixy-storefront')
assert re.fullmatch('[a-z0-9-]+',name)
assert re.fullmatch('[a-z0-9-]+',label)
result = subprocess.run([shutil.which('playwright-cli.cmd'),'-s='+session,'run-code','--filename',str(out/(name+'.js'))],
    capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=180)
text = result.stdout.replace('\r\n','\n')
if '### Result\n' not in text:
    (out.parents[2].parent/(name+'-private.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
    raise SystemExit('Headed action incomplete; inspect private diagnostic before retrying any mutation')
data = json.loads(text.split('### Result\n',1)[1].split('\n### ',1)[0])
target = out/(label+'.json')
assert not target.exists(), 'Preserve existing browser evidence'
target.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
