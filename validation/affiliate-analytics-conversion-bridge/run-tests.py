from pathlib import Path
import subprocess
root=Path(__file__).resolve().parents[2]
base='/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115'
settings=root.parent/'appsettings.Test.private.json'; settings.write_text('{}\n')
command=['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--','/opt/shopixy-toolchain/bin/docker','--host','unix:///run/user/1000/shopixy-docker.sock',
 'run','--rm','--network=none','--pull=never','--entrypoint','/bin/bash','-v',base+'/source:/src',
 '-v',base+'/appsettings.Test.private.json:/src/PointOfSale.API/appsettings.Test.json:ro',
 '-v','/home/fadya/.dotnet10-10.0.404:/dotnet:ro','-v','/home/fadya/.nuget/packages:/packages:ro',
 'postgres:18.6','/src/validation/affiliate-analytics-conversion-bridge/test.sh']
attempt=1
while (root.parent/f'tests-attempt-{attempt}-private.log').exists(): attempt+=1
with (root.parent/f'tests-attempt-{attempt}-private.log').open('w') as log:
 result=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,timeout=600)
print('Focused backend test exit:',result.returncode)
raise SystemExit(result.returncode)
