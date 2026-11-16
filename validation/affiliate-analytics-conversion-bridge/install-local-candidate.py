"""Replace only one isolated app container, retaining its predecessor for recovery."""
import importlib.util,json
from pathlib import Path
import sys,time
out=Path(__file__).parent
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)
local.assert_isolated();kind=sys.argv[1];assert kind in ('api','web')
candidates=sorted(out.glob('local-'+kind+'-candidate*.json'),key=lambda p:p.stat().st_mtime)
assert candidates
candidate=json.loads(candidates[-1].read_text())
name='affiliate-full-'+kind; prior=json.loads(local.docker('inspect',name).stdout)[0]
index=1
private=local.BASE/('analytics-'+kind+'-previous-private.json')
env=local.BASE/('analytics-'+kind+'-private.env')
while private.exists() or env.exists():
    index+=1
    private=local.BASE/f'analytics-{kind}-previous-private-{index}.json'
    env=local.BASE/f'analytics-{kind}-private-{index}.env'
private.write_text(json.dumps(prior));env.write_text('\n'.join(prior['Config']['Env'])+'\n')
args=['run','-d','--name',name,'--pull=never','--network',local.NETWORK,'--env-file','/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115/'+env.name]
for mount in prior['Mounts']:
    args += ['-v',mount['Source']+':'+mount['Destination']+('' if mount['RW'] else ':ro')]
local.docker('stop',name);local.docker('rename',name,name+'-before-analytics-'+prior['Id'][:8]+'-'+str(index))
local.docker(*args,candidate['imageId'])
for _ in range(60):
    state=json.loads(local.docker('inspect',name).stdout)[0]['State']
    if state.get('Running') and (not state.get('Health') or state['Health']['Status']=='healthy'): break
    if not state.get('Running'): raise SystemExit('Candidate stopped; predecessor retained')
    time.sleep(2)
else: raise SystemExit('Candidate readiness timed out; predecessor retained')
print(json.dumps({'component':kind,'candidate':candidate['imageId'],'isolatedNetwork':True,'previousRetained':True},indent=2))
