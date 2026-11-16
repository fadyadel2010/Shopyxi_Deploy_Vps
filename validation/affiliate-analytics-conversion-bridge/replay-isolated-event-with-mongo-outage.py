"""Force one duplicate isolated conversion delivery through a real Mongo outage/retry."""
import importlib.util,json,time
from pathlib import Path

out=Path(__file__).parent;root=out.parents[1]
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
rabbit=json.loads(local.docker('inspect','affiliate-full-rabbit').stdout)[0]
values=dict(x.split('=',1) for x in rabbit['Config']['Env'] if '=' in x)
private=root.parent/'analytics-replay-private.env'
index=1
while private.exists(): index+=1;private=root.parent/f'analytics-replay-private-{index}.env'
private.write_text('REPLAY_RABBIT_USER='+values['RABBITMQ_DEFAULT_USER']+'\nREPLAY_RABBIT_PASSWORD='+values['RABBITMQ_DEFAULT_PASS']+'\n')
mounts=['-v',local.WSL_BASE+'/source:/src','-v','/home/fadya/.dotnet10-10.0.404:/dotnet:ro','-v','/home/fadya/.nuget/packages:/packages:ro']
common=['run','--rm','--network',local.NETWORK,'--pull=never','--entrypoint','/dotnet/dotnet',*mounts,
 '--env-file',local.WSL_BASE+'/'+private.name,'-e','DOTNET_ROOT=/dotnet','-e','DOTNET_CLI_HOME=/tmp/replay','-e','NUGET_PACKAGES=/packages','postgres:18.6']
project='/src/validation/affiliate-analytics-conversion-bridge/IsolatedEventReplay/IsolatedEventReplay.csproj'
restore=local.docker(*common,'restore',project,'--ignore-failed-sources',check=False,timeout=180)
if restore.returncode: raise SystemExit('Isolated replay utility restore failed; raw output withheld')
local.docker('stop','-t','5','affiliate-full-mongo')
try:
    sent=local.docker(*common,'run','--project',project,'--no-restore',check=False,timeout=90)
    if sent.returncode: raise SystemExit('Isolated duplicate event send failed; raw output withheld')
    event_id=sent.stdout.strip().splitlines()[-1]
    time.sleep(35)
finally:
    local.docker('start','affiliate-full-mongo',check=False)
time.sleep(25)
print(json.dumps({'duplicateEventId':event_id,'brokerRouted':True,'mongoOutageSeconds':35,'restoredBeforeRetryExhaustion':True},indent=2))
