"""Replace only the isolated fixture container, preserving the old container and configuration privately."""
import importlib.util
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
spec = importlib.util.spec_from_file_location('local_stack', root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec); spec.loader.exec_module(stack)
kind = sys.argv[1]; assert kind in ('api','web')
production_candidate = sys.argv[2:] == ['--production-candidate']
assert not sys.argv[2:] or production_candidate
candidate = json.loads((out/(('production-' if production_candidate else 'local-')+kind+'-candidate.json')).read_text())
if production_candidate:
    assert candidate['securityGatePassed']
    candidate['imageId'] = candidate['image'].split('@')[1]
    observed = json.loads(stack.docker('image','inspect',candidate['image']).stdout)[0]
    assert observed['Id'] == candidate['imageId']
name = 'affiliate-full-'+kind
prior = json.loads(stack.docker('inspect',name).stdout)[0]
assert list(prior['NetworkSettings']['Networks']) == [stack.NETWORK]
assert json.loads(stack.docker('network','inspect',stack.NETWORK).stdout)[0]['Internal'] is True
assert prior['State']['Running'] and prior['Image'] != candidate['imageId']
private = root.parent/('publishing-readiness-'+kind+'-'+candidate['imageId'][7:19]+'-previous-private.json')
assert not private.exists(), 'Inspect existing replacement evidence before repeating'
private.write_text(json.dumps(prior))
env = root.parent/('publishing-readiness-'+kind+'-'+candidate['imageId'][7:19]+'-private.env')
env.write_text('\n'.join(prior['Config']['Env'])+'\n',encoding='utf-8',newline='\n')
args = ['run','-d','--name',name,'--pull=never','--network',stack.NETWORK,
    '--env-file',stack.WSL_BASE+'/'+env.name]
for mount in prior['Mounts']:
    assert mount['Type'] == 'bind', 'Review volume mounts before replacement'
    args += ['-v',mount['Source']+':'+mount['Destination']+('' if mount['RW'] else ':ro')]
stack.docker('stop',name)
stack.docker('rename',name,name+'-before-readiness-'+prior['Id'][:8])
result = stack.docker(*args,candidate['imageId'],check=False)
assert result.returncode == 0, 'Candidate did not start; previous stopped container is retained'
print(json.dumps({'component':kind,'installedImage':candidate['imageId'],'onlyDedicatedInternalNetwork':True,
    'previousContainerRetained':True,'productionMutations':False},indent=2))
