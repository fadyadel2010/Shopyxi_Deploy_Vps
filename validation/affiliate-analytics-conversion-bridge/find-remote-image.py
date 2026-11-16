"""Read-only lookup of an imported CRI image by sealed source label."""
import subprocess,sys
source=sys.argv[1]
remote="""import json,subprocess
source=%r
lines=subprocess.check_output(['/usr/local/bin/k3s','ctr','images','list'],text=True).splitlines()
for line in lines:
 parts=line.split();ref=parts[0] if parts else ''
 if not ref.startswith(('docker.io/shopixy/api@','docker.io/shopixy/web@')): continue
 try:d=json.loads(subprocess.check_output(['/usr/local/bin/k3s','crictl','inspecti',ref],text=True,stderr=subprocess.DEVNULL))
 except Exception:continue
 labels=d.get('info',{}).get('imageSpec',{}).get('config',{}).get('Labels',{})
 if labels.get('shopixy.io/source-input-sha256')==source: print(ref)
""" % source
r=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=300)
assert r.returncode==0 and r.stdout.strip(),'No sealed imported image found'
print(r.stdout.strip())
