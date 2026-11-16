"""Build against the existing pinned offline toolchain, without Production access."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
base = '/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115'
command = ['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--','/opt/shopixy-toolchain/bin/docker',
    '--host','unix:///run/user/1000/shopixy-docker.sock','run','--rm','--network=none','--pull=never',
    '--entrypoint','/bin/bash','-v',base+'/source:/src',
    '-v','/home/fadya/.dotnet10-10.0.404:/dotnet:ro','-v','/home/fadya/.nuget/packages:/packages:ro',
    'postgres:18.6','/src/validation/affiliate-analytics-conversion-bridge/build.sh']
with (root.parent/'build-private.log').open('w') as log:
    result = subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,timeout=1200)
print('Build exit:',result.returncode,'(diagnostic retained outside source)')
raise SystemExit(result.returncode)
