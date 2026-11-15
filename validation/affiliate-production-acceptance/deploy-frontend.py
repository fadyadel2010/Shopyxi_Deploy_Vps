"""Bounded frontend-only archive release. Run remotely after verified archive transfer."""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path('/var/lib/shopixy/affiliate-20261115')
ARCHIVE = ROOT / 'web.tar.gz'
ARCHIVE_SHA = '10096bf675b64af495ebab19925c1422973f693c72cc25529833c951cedaeeaf'
OLD = 'docker.io/shopixy/web@sha256:756879210827a50c84d8d42da66e2d6e2235907e1af33930a2a450cab0f46682'
NEW = 'docker.io/shopixy/web@sha256:1a594262000458743141e30bc416a51d05d7717aa565a33621b961507a3670d9'
TAG = 'docker.io/shopixy/web:affiliate-20261115-cf0a986217d2'
SOURCE = '0de9f025e0825ad1a9b726d80dbaca487a6e2f0658ef938a95e212a9b95dc11b'


def command(args, timeout=180):
    result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError('Release command failed: ' + args[0] + '; raw output retained only on server')
    return result.stdout


def kube(*args, timeout=180):
    return command(['/usr/local/bin/kubectl', '--request-timeout=30s', *args], timeout)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def snapshot(replacement=None):
    d = json.loads(kube('get', 'deployment', 'shopixy-web', '-n', 'shopixy-app', '-o', 'json'))
    spec = d['spec']
    index = next(i for i, c in enumerate(spec['template']['spec']['containers']) if c['name'] == 'web')
    expected = copy.deepcopy(spec)
    if replacement:
        expected['template']['spec']['containers'][index]['image'] = replacement
    selector = ','.join(k + '=' + v for k, v in spec['selector']['matchLabels'].items())
    pods = json.loads(kube('get', 'pods', '-n', 'shopixy-app', '-l', selector, '-o', 'json'))['items']
    pods = [p for p in pods if not p['metadata'].get('deletionTimestamp')]
    return {'uid': d['metadata']['uid'], 'resourceVersion': d['metadata']['resourceVersion'],
        'generation': d['metadata']['generation'], 'observedGeneration': d.get('status', {}).get('observedGeneration'),
        'index': index, 'image': spec['template']['spec']['containers'][index]['image'],
        'specSha256': digest(spec), 'expectedImageOnlySpecSha256': digest(expected),
        'replicas': spec.get('replicas'), 'strategy': spec['strategy']['type'],
        **{k: d.get('status', {}).get(k, 0) for k in ('readyReplicas', 'updatedReplicas', 'availableReplicas')},
        'pods': [{'name': p['metadata']['name'], 'node': p['spec']['nodeName'],
            'containers': [{k: c.get(k) for k in ('name', 'imageID', 'ready', 'restartCount')}
                for c in p.get('status', {}).get('containerStatuses', [])]} for p in pods]}


def ready(s):
    return (s['replicas'] == s['readyReplicas'] == s['updatedReplicas'] == s['availableReplicas'] == 1
        and s['generation'] == s['observedGeneration'] and len(s['pods']) == 1
        and all(c['ready'] for c in s['pods'][0]['containers']))


def other_images():
    return {name: [(c['name'], c['image']) for c in json.loads(kube('get', 'deployment', name,
        '-n', 'shopixy-app', '-o', 'json'))['spec']['template']['spec']['containers']]
        for name in ('shopixy-api', 'shopixy-storefront')}


assert not (ROOT / 'release-started.json').exists(), 'Existing attempt: inspect state, do not retry blindly'
assert hashlib.sha256(ARCHIVE.read_bytes()).hexdigest() == ARCHIVE_SHA
nodes = json.loads(kube('get', 'nodes', '-o', 'json'))['items']
assert len(nodes) == 1, 'Archive release requires the established single-node cluster'
before = snapshot(NEW)
assert ready(before) and before['image'] == OLD
others = other_images()
command(['/usr/local/bin/k3s', 'crictl', 'inspecti', OLD])
command(['/usr/local/bin/k3s', 'ctr', 'images', 'import', '--digests', str(ARCHIVE)], timeout=600)
imported = json.loads(command(['/usr/local/bin/k3s', 'crictl', 'inspecti', TAG]))
labels = imported.get('info', {}).get('imageSpec', {}).get('config', {}).get('Labels', {})
assert labels.get('shopixy.io/source-input-sha256') == SOURCE
# The imported digest reference must resolve to the SAME CRI config identity as
# the exact release tag. Manifest/index/config identifiers are not interchangeable.
try:
    identity = json.loads(command(['/usr/local/bin/k3s', 'crictl', 'inspecti', NEW]))['status']
except RuntimeError:
    listing = command(['/usr/local/bin/k3s', 'ctr', 'images', 'list'])
    tag_line = next(line for line in listing.splitlines() if line.split()[0] == TAG)
    assert NEW.split('@')[1] in tag_line.split(), 'Imported manifest digest mismatch'
    command(['/usr/local/bin/k3s', 'ctr', 'images', 'tag', TAG, NEW])
    identity = json.loads(command(['/usr/local/bin/k3s', 'crictl', 'inspecti', NEW]))['status']
assert identity['id'] == imported['status']['id'] and NEW in identity.get('repoDigests', [])
imagepath = '/spec/template/spec/containers/' + str(before['index']) + '/image'
patch = [{'op': 'test', 'path': '/metadata/uid', 'value': before['uid']},
    {'op': 'test', 'path': '/metadata/resourceVersion', 'value': before['resourceVersion']},
    {'op': 'test', 'path': imagepath, 'value': OLD}, {'op': 'replace', 'path': imagepath, 'value': NEW}]
args = ['patch', 'deployment', 'shopixy-web', '-n', 'shopixy-app', '--type=json', '-p', json.dumps(patch)]
kube(*args, '--dry-run=server')
receipt = {'startedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'before': before,
    'intendedImage': NEW, 'rollbackImage': OLD, 'archiveSha256': ARCHIVE_SHA,
    'sourceIndexSha256': SOURCE, 'releaseTransport': 'OCI archive import; registry push rejected',
    'imageOnly': True, 'businessDataWrites': False, 'schemaOrInfrastructureChanged': False}
(ROOT / 'release-started.json').write_text(json.dumps(receipt, indent=2))
kube(*args)
kube('rollout', 'status', 'deployment/shopixy-web', '-n', 'shopixy-app', '--timeout=480s', timeout=510)
after = snapshot()
assert ready(after) and after['uid'] == before['uid'] and after['image'] == NEW
assert after['specSha256'] == before['expectedImageOnlySpecSha256']
assert other_images() == others
allowed = {identity['id']} | {r.split('@')[-1] for r in identity.get('repoDigests', [])}
container = next(c for c in after['pods'][0]['containers'] if c['name'] == 'web')
assert container['imageID'].split('@')[-1] in allowed
receipt.update({'after': after, 'runtimeConfigId': identity['id'], 'verifiedRepoDigests': identity['repoDigests'],
    'otherWorkloadImagesUnchanged': True, 'immutableIdentityVerified': True,
    'completedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()})
(ROOT / 'release-verified.json').write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt, indent=2))
