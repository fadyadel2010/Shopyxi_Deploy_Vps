"""Stage only bridge source and this task's sanitized evidence; no private/generated binaries."""
import subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2]
allowed=('PointOfSale.API/','PointOfSale.Core/','PointOfSale.Infrastructure/','PointOfSale.Tests/Affiliate/','PointOfSale.Web/')
changed=subprocess.check_output(['git','ls-files','--modified','--others','--exclude-standard','-z'],cwd=root).decode().strip('\0').split('\0')
source=[n for n in changed if n.startswith(allowed) or n=='deploy/automatic-subdomains/successor.Dockerfile']
if source:subprocess.run(['git','add','--',*source],cwd=root,check=True)
evidence=root/'validation/affiliate-analytics-conversion-bridge'
files=[p.relative_to(root).as_posix() for p in evidence.rglob('*') if p.is_file() and p.suffix in ('.py','.js','.jsx','.json','.md','.sql','.sh','.png','.cs','.csproj','.txt') and not any(x in p.parts for x in ('bin','obj','__pycache__','results')) and 'private' not in p.name]
subprocess.run(['git','add','-f','--',*files],cwd=root,check=True)
print(f'Staged {len(source)} source paths and {len(files)} reviewed validation paths.')
