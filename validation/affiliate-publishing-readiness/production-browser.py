"""Read-only Production browser evidence; never serialize cookies, credentials or business row bodies."""
import json
from pathlib import Path
import shutil
import subprocess
import sys

out=Path(__file__).parent
name=sys.argv[1];assert name in ('production-context','production-readonly')
result=subprocess.run([shutil.which('playwright-cli.cmd'),'-s=shopixy-r10-owner','run-code','--filename',str(out/(name+'.js'))],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=180)
text=result.stdout.replace('\r\n','\n')
if '### Result\n' not in text:
    (out.resolve().parents[2]/(name+'-private.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
    raise SystemExit('Read-only Production browser incomplete; inspect private diagnostic, never retry a business mutation')
data=json.loads(text.split('### Result\n',1)[1].split('\n### ',1)[0])
(out/(name+'.json')).write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
