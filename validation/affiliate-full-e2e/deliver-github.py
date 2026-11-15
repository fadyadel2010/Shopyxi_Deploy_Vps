"""Atomic, non-force fast-forward of reviewed task branch and main; no credential export."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
branch = 'affiliate/full-e2e-20261115'
baseline = '8365a92416463136418065f040e8b7f84bcff9fe'
expected_main = sys.argv[1] if len(sys.argv) == 2 else baseline
target = root/'validation/affiliate-full-e2e/github-delivery.json'
assert not target.exists(), 'Retain existing delivery receipt; inspect before another run'
def git(*args): return subprocess.check_output(['git',*args],cwd=root,text=True).strip()
assert git('branch','--show-current') == branch
assert not git('status','--porcelain'), 'Commit and review all task-owned changes before delivery'
head = git('rev-parse','HEAD')
evidence = root/'validation/affiliate-full-e2e'
candidate = json.loads((evidence/'production-api-candidate.json').read_text())
deployment = json.loads((evidence/'production-deployment.json').read_text())
assert deployment['after']['api']['image'] == candidate['image'] and deployment['after']['api']['generation'] == 50
assert deployment['readOnlyApplicationProbe']['status'] == 200
entries = [line.split('  ',1) for line in (evidence/'production-api-source-input-SHA256SUMS.txt').read_text().splitlines()]
entries.append([candidate['additionalEmbeddedThemeResourceSha256'],'theme-authority/shopixy-theme-authority.json'])
batch = subprocess.run(['git','cat-file','--batch'],cwd=root,
    input=''.join(head+':'+name+'\n' for _,name in entries).encode(),capture_output=True,check=True).stdout
offset = 0
for expected,name in entries:
    end = batch.index(b'\n',offset)
    length = int(batch[offset:end].split()[2])
    assert hashlib.sha256(batch[end+1:end+1+length]).hexdigest() == expected, 'Committed source differs from deployed build: '+name
    offset = end+1+length+1
subprocess.run(['git','merge-base','--is-ancestor',expected_main,head],cwd=root,check=True)
remote = {line.split()[1]:line.split()[0] for line in git('ls-remote','origin','refs/heads/main','refs/heads/'+branch).splitlines()}
assert remote.get('refs/heads/main') == expected_main, 'Remote main moved independently; stop and review'
if 'refs/heads/'+branch in remote:
    assert remote['refs/heads/'+branch] == expected_main, 'Remote task branch moved independently; stop and review'
subprocess.run(['git','push','--atomic','origin','HEAD:refs/heads/'+branch,'HEAD:refs/heads/main'],cwd=root,check=True)
subprocess.run(['git','fetch','--no-tags','origin','main',branch],cwd=root,check=True)
refs = {line.split()[1]:line.split()[0] for line in git('ls-remote','origin','refs/heads/main','refs/heads/'+branch).splitlines()}
assert set(refs.values()) == {head} and len(refs) == 2
tree = git('rev-parse','HEAD^{tree}')
assert git('rev-parse','origin/main^{tree}') == tree
assert git('rev-parse','origin/'+branch+'^{tree}') == tree
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'repository':'https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git', 'deliveredCommit':head,
    'previousRemoteMain':expected_main, 'remoteRefs':refs, 'tree':tree,
    'bothBranchesMatch':True, 'fetchedRemoteTreesMatchLocal':True, 'atomicPush':True,'forceUsed':False,
    'committedInputsMatchDeployedApiBuild':len(entries), 'deployedApiImage':candidate['image'],
    'credentialsExported':False}
target.write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps(receipt,indent=2))
