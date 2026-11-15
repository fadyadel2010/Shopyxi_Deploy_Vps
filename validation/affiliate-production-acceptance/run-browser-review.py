"""Capture only sanitized browser results. CLI source echo and arbitrary errors are never published."""
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys

session, script, output = sys.argv[1:]
result = subprocess.run([shutil.which('playwright-cli.cmd'), '-s=' + session, 'run-code', '--filename', script],
    capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=180)
text = result.stdout.replace('\r\n', '\n')
if '### Result\n' not in text:
    error = text.split('### Ran Playwright code', 1)[0]
    error = re.sub(r'[\w.+-]+@[\w.-]+', '[mailbox withheld]', error)
    error = re.sub(r'([?&](?:token|code|email)=)[^\s&"<>]+', r'\1[withheld]', error, flags=re.I)
    print(error[:1800]); raise SystemExit('Review incomplete; no acceptance result recorded')
safe = text.split('### Result\n', 1)[1].split('\n### ', 1)[0]
def sanitize(value):
    if isinstance(value, dict): return {k:sanitize(v) for k,v in value.items()}
    if isinstance(value, list): return [sanitize(v) for v in value]
    if isinstance(value, str):
        value = re.sub(r'[\w.+-]+@[\w.-]+', '[mailbox withheld]', value)
        return re.sub(r'([?&](?:token|code|email)=)[^\s&"<>]+', r'\1[withheld]', value, flags=re.I)
    return value
data = sanitize(json.loads(safe))
Path(output).write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding='utf-8')
print(json.dumps(data, indent=2, ensure_ascii=True))
