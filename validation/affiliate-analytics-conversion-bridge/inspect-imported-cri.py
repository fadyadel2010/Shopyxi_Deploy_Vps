"""Read-only candidate CRI alias discovery; no runtime environment values."""
import subprocess
remote=r'''
import json,subprocess
rows={r.split()[0]:r.split()[2] for r in subprocess.check_output(['/usr/local/bin/k3s','ctr','images','list'],text=True).splitlines()[1:] if r.split()}
for component,digest in [('api','8c954ba73be0321da387bf258b90e751926f092ef33c68c3dc51e0dc48aa1865'),('web','4f57cdb948c4c9a0f91bfdb0b283590a1976cf4f80f7f28fcbdd190726168219')]:
 ref='docker.io/shopixy/'+component+'@sha256:'+digest
 x=json.loads(subprocess.check_output(['/usr/local/bin/k3s','crictl','inspecti',ref],text=True))
 print(json.dumps({'component':component,'status':x['status'],'references':[{'ref':ref,'actual':rows.get(ref),'expected':ref.split('@')[1]} for ref in x['status'].get('repoDigests',[])]}))
'''
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=180)
assert r.returncode==0;print(r.stdout)
