"""Run bounded headed evidence code; keep CLI/source echoes and raw diagnostics private."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import re
sys.stdout.reconfigure(encoding='utf-8')

root = Path(__file__).resolve().parents[2]
session, name = sys.argv[1:3]
label = sys.argv[3] if len(sys.argv) == 4 else name
assert re.fullmatch(r'[A-Za-z0-9-]+', label)
assert session in ('affiliate-full-owner','affiliate-full-affiliate','affiliate-full-customer')
script = Path(__file__).parent/(name+'.js')
assert script.is_file()
result = subprocess.run([shutil.which('playwright-cli.cmd'),'-s='+session,'run-code','--filename',str(script)],
    capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=180)
text = result.stdout.replace('\r\n','\n')
if '### Result\n' not in text:
    (root.parent/(name+'-browser-private.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
    raise SystemExit('Headed action incomplete; inspect private diagnostic before retrying any mutation.')
safe = json.loads(text.split('### Result\n',1)[1].split('\n### ',1)[0])
(Path(__file__).parent/(label+'.json')).write_text(json.dumps(safe,indent=2,ensure_ascii=False)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(safe,indent=2,ensure_ascii=False))
