"""Pinned npm-ci test/type/build gate in a private copied work area; no dependency changes."""
import hashlib
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
target = root.parent/'analytics-web'
assert not target.exists(), 'Preserve the prior gate; inspect it instead of overwriting'
shutil.copytree(root/'PointOfSale.Web',target,ignore=shutil.ignore_patterns('node_modules','dist'))
before = hashlib.sha256((target/'package-lock.json').read_bytes()).hexdigest()
log = root.parent/'web-gates-private.log'
with log.open('w',encoding='utf-8') as output:
    for command in ([shutil.which('npm.cmd'),'ci'],
        [shutil.which('node.exe'),'node_modules/vitest/vitest.mjs','run','src/modules/affiliate/pages/portal/AnalyticsTab.conversion.test.tsx'],
        [shutil.which('node.exe'),'node_modules/typescript/bin/tsc','--noEmit'],
        [shutil.which('node.exe'),'node_modules/vite/bin/vite.js','build']):
        result=subprocess.run(command,cwd=target,stdout=output,stderr=subprocess.STDOUT,text=True,timeout=900)
        if result.returncode: raise SystemExit('Web gate failed; private diagnostic retained')
assert before == hashlib.sha256((target/'package-lock.json').read_bytes()).hexdigest()
print('Focused frontend tests, TypeScript and production build passed; lockfile unchanged.')
