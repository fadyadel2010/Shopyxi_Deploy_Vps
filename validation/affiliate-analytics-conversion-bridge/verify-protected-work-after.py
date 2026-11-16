"""Verify the protected original worktree and preservation patch without writing there."""
import datetime,hashlib,json,subprocess
from pathlib import Path
out=Path(__file__).parent;original=Path('D:/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source')
before=json.loads((out/'protected-work-before.json').read_text())
status=subprocess.check_output(['git','status','--porcelain'],cwd=original)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest() if p.is_file() else None
assert hashlib.sha256(status).hexdigest()==before['statusSha256']
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=original,text=True).strip()==before['originalHead']
assert all(sha(original/f['path'])==f['sha256'] for f in before['files'])
assert sha(original/'docs/local-work/preserved-working-differences.patch')==before['preservedPatchSha256']
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'originalHeadUnchanged':True,
 'workingEntries':before['workingEntries'],'allProtectedFilesUnchanged':True,'preservedPatchUnchanged':True}
target=out/'protected-work-after.json';assert not target.exists();target.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt,indent=2))
