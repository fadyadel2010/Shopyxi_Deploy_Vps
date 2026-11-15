"""Reverify exact built API/Web inputs against the index (or an explicit commit)."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

root=Path(__file__).resolve().parents[2]
out=Path(__file__).parent
parser=argparse.ArgumentParser()
parser.add_argument('revision',nargs='?',default='')
parser.add_argument('--check-only',action='store_true')
args=parser.parse_args()
revision=args.revision
if revision:
 revision=subprocess.check_output(['git','rev-parse',revision+'^{commit}'],cwd=root,text=True).strip()
api=[{'path':name,'sha256':digest} for digest,name in (line.split('  ',1) for line in (out/'production-api-source-input-SHA256SUMS.txt').read_text().splitlines())]
web=json.loads((root.parent/'publishing-readiness-production-web/source-inputs.json').read_text())
counts={}
for kind,entries in (('api',api),('web',web)):
 batch=subprocess.run(['git','cat-file','--batch'],cwd=root,input=''.join(revision+':'+item['path']+'\n' for item in entries).encode(),capture_output=True,check=True).stdout
 offset=0
 for item in entries:
  end=batch.index(b'\n',offset)
  header=batch[offset:end].split();assert header[1]==b'blob','Missing source input: '+item['path']
  length=int(header[2]);actual=hashlib.sha256(batch[end+1:end+1+length]).hexdigest()
  assert actual==item['sha256'],'Built source mismatch: '+item['path']
  offset=end+1+length+1
 counts[kind]=len(entries)
theme=subprocess.check_output(['git','show',revision+':theme-authority/shopixy-theme-authority.json'],cwd=root)
assert theme==(root.parent/'publishing-readiness-production-api/theme-authority/shopixy-theme-authority.json').read_bytes()
assert not subprocess.check_output(['git','diff','--name-only','--','PointOfSale.API','PointOfSale.Core','PointOfSale.Infrastructure','PointOfSale.Tests/Affiliate','PointOfSale.Web'],cwd=root),'Unstaged source changes exist'
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'authority':revision or 'exact staged index','builtInputsMatched':counts,'embeddedThemeMatches':True,'unstagedAffectedSourceChanges':False,'passed':True}
if not args.check_only:(out/('source-parity-commit.json' if revision else 'source-parity-index.json')).write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
