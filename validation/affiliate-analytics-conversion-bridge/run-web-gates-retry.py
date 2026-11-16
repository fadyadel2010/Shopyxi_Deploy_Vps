"""Bounded retry after a focused assertion correction; retains first diagnostic."""
import hashlib
from pathlib import Path
import shutil,subprocess
root=Path(__file__).resolve().parents[2]; target=root.parent/'analytics-web'
assert target.is_dir() and (root.parent/'web-gates-private.log').is_file()
shared=root.parent/'node_modules'
if not shared.exists():
    subprocess.run(['cmd','/c','mklink','/J',str(shared),str(target/'node_modules')],check=True,capture_output=True)
for sibling in ('PointOfSale.Storefront','PointOfSale.CommerceHub','theme-authority'):
    shutil.copytree(root/sibling,root.parent/sibling,dirs_exist_ok=True)
for relative in ('src/modules/affiliate/pages/portal/AnalyticsTab.conversion.test.tsx',
    'src/modules/affiliate/pages/portal/AnalyticsTab.jsx','src/modules/affiliate/pages/portal/AnalyticsTab.d.ts',
    'src/modules/affiliate/i18n/translations.js'):
    shutil.copy2(root/'PointOfSale.Web'/relative,target/relative)
lock=hashlib.sha256((target/'package-lock.json').read_bytes()).hexdigest()
commands=([shutil.which('node.exe'),'node_modules/vitest/vitest.mjs','run','src/modules/affiliate/pages/portal/AnalyticsTab.conversion.test.tsx'],
 [shutil.which('node.exe'),'node_modules/typescript/bin/tsc','--noEmit'],
 [shutil.which('node.exe'),'node_modules/vite/bin/vite.js','build'])
attempt=1
while (root.parent/f'web-gates-retry-{attempt}-private.log').exists(): attempt+=1
with (root.parent/f'web-gates-retry-{attempt}-private.log').open('w') as output:
    for command in commands:
        result=subprocess.run(command,cwd=target,stdout=output,stderr=subprocess.STDOUT,text=True,timeout=900)
        if result.returncode: raise SystemExit('Web retry failed; diagnostic retained')
assert lock==hashlib.sha256((target/'package-lock.json').read_bytes()).hexdigest()
print('3 focused frontend tests, TypeScript and production build passed; lockfile unchanged.')
