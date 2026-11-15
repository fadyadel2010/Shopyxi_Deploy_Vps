"""Read-only containerd/CRI index, leaf and alias evidence for exact candidates."""
import json
from pathlib import Path
import subprocess

out=Path(__file__).parent
inputs=json.loads((out/'release-input.json').read_text())
remote=r'''
import json,subprocess
inputs=INPUTS
def call(*args):
 r=subprocess.run(['/usr/local/bin/k3s',*args],capture_output=True,text=True,timeout=60)
 return {'exitCode':r.returncode,'stdout':r.stdout,'stderr':r.stderr}
listing=call('ctr','images','list')['stdout'].splitlines()[1:]
result={}
for kind,item in inputs['components'].items():
 digest=item['image'].split('@')[1]
 def visit(digest):
  node=json.loads(call('ctr','content','get',digest)['stdout'])
  return [{'digest':digest,'config':node.get('config',{}).get('digest'),'layers':len(node.get('layers',[]))}] if 'manifests' not in node else [leaf for child in node['manifests'] for leaf in visit(child['digest'])]
 leaves=visit(digest)
 inspect=call('crictl','inspecti',item['image'])
 parsed=json.loads(inspect['stdout']) if inspect['exitCode']==0 else {}
 result[kind]={'index':digest,'leaves':leaves,'references':[line.split()[:3] for line in listing if any(x in line for x in (digest,item['runtimeConfigId'],*[l['digest'] for l in leaves]))],
 'criStatus':parsed.get('status'),'criInspectExitCode':inspect['exitCode']}
print(json.dumps(result))
'''.replace('INPUTS',repr(inputs))
result=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo python3 -'],input=remote,text=True,capture_output=True,timeout=180)
assert result.returncode==0,'Read-only image inspection failed'
data=json.loads(result.stdout)
(out/'image-admission-diagnosis.json').write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
