"""Read remote heads directly; never infer GitHub delivery from a local commit."""
import argparse
import datetime
import json
from pathlib import Path
import subprocess

parser=argparse.ArgumentParser()
parser.add_argument('implementation_commit')
parser.add_argument('--check-only',action='store_true')
args=parser.parse_args()
root=Path(__file__).resolve().parents[2]
def git(*values):return subprocess.check_output(['git',*values],cwd=root,text=True).strip()
assert git('remote','get-url','origin')=='https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git'
assert git('branch','--show-current')=='affiliate/publishing-readiness-20261115'
head=git('rev-parse','HEAD')
implementation=git('rev-parse',args.implementation_commit+'^{commit}')
subprocess.run(['git','merge-base','--is-ancestor',implementation,head],cwd=root,check=True)
refs=dict(reversed(line.split()) for line in git('ls-remote','--heads','origin','refs/heads/main','refs/heads/affiliate/publishing-readiness-20261115').splitlines())
assert len(refs)==2 and set(refs.values())=={head},'Remote delivery is incomplete or changed; stop without force-push'
if implementation!=head:
 changed=git('diff','--name-only',implementation,head).splitlines()
 assert changed and all(name.startswith('validation/affiliate-publishing-readiness/') for name in changed),'Closure commit changed deployable source'
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'repository':'https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git',
 'implementationCommit':implementation,'observedLocalHead':head,'remoteHeads':refs,'bothRemoteHeadsMatchLocalHead':True,
 'implementationIsAncestor':True,'closureChangesEvidenceOnly':implementation!=head,'forcePushUsed':False,'passed':True}
if not args.check_only:(Path(__file__).parent/'git-delivery.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
