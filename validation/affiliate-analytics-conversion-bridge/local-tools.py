"""Existing isolated stack access only. No setup, reset or Production credentials."""
import json
from pathlib import Path
import subprocess

BASE = Path(__file__).resolve().parents[3]
DOCKER = ['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--','env',
    'DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock',
    'DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker','/opt/shopixy-toolchain/bin/docker']
NETWORK = 'affiliate-full-e2e-20261115'
WSL_BASE = '/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115'

def docker(*args, input=None, check=True, timeout=180):
    r = subprocess.run([*DOCKER,*args],input=input,capture_output=True,text=True,timeout=timeout)
    if check and r.returncode:
        (BASE/'local-command-private.log').write_text(r.stdout+r.stderr)
        raise RuntimeError('Isolated command failed; diagnostic kept outside source')
    return r

def query(sql):
    assert sql.lstrip().startswith('BEGIN') and 'READ ONLY' in sql
    return docker('exec','-i','affiliate-full-postgres','psql','-U','affiliate_fixture','-d','affiliate_full_e2e',
        '-X','-qAt','-v','ON_ERROR_STOP=1',input=sql).stdout.strip()

def assert_isolated():
    assert json.loads(docker('network','inspect',NETWORK).stdout)[0]['Internal']
    assert query('BEGIN READ ONLY; SELECT current_database(); ROLLBACK;') == 'affiliate_full_e2e'
