"""Observe the untouched original worktree and preserved patch; never repair historical hash drift."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

out=Path(__file__).parent
original=Path('D:/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source')
prior=json.loads((original/'validation/affiliate-production-acceptance/preservation-verification.json').read_text())
digest=lambda path:hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None
checks=[{'path':item['path'],'matchesHistoricalFullFileHash':digest(original/item['path'])==item['sha256']} for item in prior['protectedDifferences']]
patch=digest(original/'docs/local-work/preserved-working-differences.patch')
assert patch==prior['preservedPatchSha256'],'Preserved patch changed; stop delivery without altering it'
status=subprocess.check_output(['git','status','--porcelain'],cwd=original)
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'originalWorktreeEditedOrStagedByThisContinuation':False,
    'originalHead':subprocess.check_output(['git','rev-parse','HEAD'],cwd=original,text=True).strip(),
    'observedWorkingEntries':len(status.decode().splitlines()),'statusSha256':hashlib.sha256(status).hexdigest(),
    'preservedPatchSha256':patch,'preservedPatchUnchanged':True,'historicalProtectedFileChecks':checks,
    'historicalDriftWasNotReset':True,'note':'Overlapping readiness edits were already present in the original directory at continuation start. Historical full-file hash drift is reported, not discarded.'}
(out/'protected-worktree-verification.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps({'preservedPatchUnchanged':True,'observedWorkingEntries':receipt['observedWorkingEntries'],
    'historicalFullFileHashMismatches':[item['path'] for item in checks if not item['matchesHistoricalFullFileHash']],
    'originalWorktreeEditedOrStagedByThisContinuation':False},indent=2))
