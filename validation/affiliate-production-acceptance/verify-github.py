"""Read-only exact private-repository checks using existing GCM credential only in memory."""
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
repo = 'fadyadel2010/Shopyxi_Deploy_Vps'
url = 'https://github.com/' + repo + '.git'
env = dict(os.environ, GCM_INTERACTIVE='Never', GIT_TERMINAL_PROMPT='0')
def git(*args):
    return subprocess.check_output(['git', *args], cwd=root).decode().strip()
assert git('remote', 'get-url', 'origin') == url
p = subprocess.run(['git', 'credential', 'fill'], input=b'protocol=https\nhost=github.com\n\n', capture_output=True, env=env, cwd=root)
assert p.returncode == 0, 'Existing Git credential unavailable; no values printed'
creds = dict(line.split('=', 1) for line in p.stdout.decode().splitlines() if '=' in line)
token = creds.get('password')
assert token
def api(path):
    result = subprocess.run(['gh', 'api', path], capture_output=True, env=dict(env, GH_TOKEN=token), timeout=90)
    assert result.returncode == 0, 'Read-only GitHub API check failed; raw output withheld'
    return json.loads(result.stdout)
metadata = api('repos/' + repo)
assert metadata['full_name'].lower() == repo.lower() and metadata['private'] and metadata['permissions']['push']
result = {'verifiedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'repository': url,
    'private': True, 'pushPermission': True, 'defaultBranch': metadata['default_branch'],
    'credentialMethod': 'Existing GCM credential in child-process memory only; no login or credential changes'}
mode = 'prerequisite'
if sys.argv[1:] == ['--remote']:
    mode = 'remote-verification'
    head = git('rev-parse', 'HEAD')
    branch = git('branch', '--show-current')
    main = api('repos/' + repo + '/git/ref/heads/main')['object']['sha']
    feature = api('repos/' + repo + '/git/ref/heads/' + branch)['object']['sha']
    assert head == main == feature
    tree = api('repos/' + repo + '/git/trees/' + head + '?recursive=1')
    assert not tree.get('truncated')
    remote = {r['path']: r['sha'] for r in tree['tree'] if r['type'] == 'blob'}
    local = {line.split('\t', 1)[1]: line.split()[2] for line in git('ls-tree', '-r', 'HEAD').splitlines()}
    assert remote == local
    result.update({'localCommit': head, 'remoteMainCommit': main, 'remoteFeatureCommit': feature,
        'bothBranchesMatch': True, 'completeRemoteTreeMatchesLocal': True, 'remoteBlobCount': len(remote)})
target = root / 'validation/affiliate-production-acceptance' / ('github-' + mode + '.json')
target.write_text(json.dumps(result, indent=2), encoding='utf-8', newline='\n')
print(json.dumps(result, indent=2))
