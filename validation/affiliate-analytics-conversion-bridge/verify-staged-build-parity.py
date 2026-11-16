"""Compare sealed production build inputs with the staged source, allowing only Git EOL normalization."""
import hashlib,json,subprocess,io
from pathlib import Path
root=Path(__file__).resolve().parents[2];out=Path(__file__).parent
manifest=out/'production-api-build-evidence-final/source-input-SHA256SUMS.txt'
index=subprocess.check_output(['git','ls-files','--stage','-z'],cwd=root).decode().split('\0')
objects={entry.split('\t',1)[1]:entry.split()[1] for entry in index if entry}
api_inputs=[line.split('  ',1)[1] for line in manifest.read_text().splitlines()]
inputs=json.loads((root.parent/'analytics-production-web-2/source-inputs.json').read_text())
names=sorted(set(api_inputs+[i['path'] for i in inputs]));assert all(n in objects for n in names)
raw=subprocess.check_output(['git','cat-file','--batch'],cwd=root,input=('\n'.join(objects[n] for n in names)+'\n').encode())
stream=io.BytesIO(raw);blobs={}
for name in names:
 header=stream.readline().decode().strip().split();assert header[1]=='blob'
 blobs[name]=stream.read(int(header[2]));assert stream.read(1)==b'\n'
checks=normalized=0
for line in manifest.read_text().splitlines():
 expected,name=line.split('  ',1);file=root/name
 assert file.is_file() and hashlib.sha256(file.read_bytes()).hexdigest()==expected,name
 staged=blobs[name]
 if hashlib.sha256(staged).hexdigest()!=expected:
  assert staged.replace(b'\r\n',b'\n')==file.read_bytes().replace(b'\r\n',b'\n'),name
  normalized+=1
 checks+=1
web=json.loads((out/'production-web-candidate-2.json').read_text())
for item in inputs:
 file=root/item['path'];assert hashlib.sha256(file.read_bytes()).hexdigest()==item['sha256']
 staged=blobs[item['path']]
 assert staged.replace(b'\r\n',b'\n')==file.read_bytes().replace(b'\r\n',b'\n'),item['path']
data={'apiBuildInputsVerified':checks,'webBuildInputsVerified':len(inputs),'apiGitEolNormalizationsOnly':normalized,
 'stagedSourceMatchesDeployedBuildInputs':True,'webSourceInputSha256':web['sourceInputSha256']}
target=out/'staged-build-parity.json';assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
