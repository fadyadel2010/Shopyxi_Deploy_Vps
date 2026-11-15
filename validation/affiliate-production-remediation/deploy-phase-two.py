"""Bounded Affiliate phase-two release: additive schema/grants plus API/Web image patches.
Run on the single Production node after transferring the exact verified artifacts.
"""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path('/var/lib/shopixy/affiliate-return-20261115')
API_ARCHIVE, WEB_ARCHIVE = ROOT / 'api.tar.gz', ROOT / 'web.tar.gz'
API_SHA = 'c07dcd26c115f251feaee6b74157c036f14ea3d7f77a7f871be50928c596d40b'
WEB_SHA = '4bad0c77034499f0ffc1509a4c6f2e7f3e76876b25baeb935c4c39645ccf106b'
SOURCE = 'a909b4464ff8c1917ba2d0381cc888a5a9e68a5330569527e9964950ca30d942'
API_TAG = 'docker.io/shopixy/api:affiliate-return-20261115-a909b4464ff8'
WEB_TAG = 'docker.io/shopixy/web:affiliate-return-20261115-a909b4464ff8'
OLD_API = 'docker.io/shopixy/api@sha256:d3f10ae2cc93eb1fdddb7bd89c55726ca3f6d0ea1e4dbbf7825f47aaa19dbcb9'
OLD_WEB = 'docker.io/shopixy/web@sha256:1a594262000458743141e30bc416a51d05d7717aa565a33621b961507a3670d9'
STOREFRONT = 'docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df'

def command(args, timeout=180, input_text=None):
    result = subprocess.run(args, input=input_text, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        (ROOT/'last-command-error.log').write_text(result.stderr)
        raise RuntimeError('Release command failed: ' + args[0] + '; raw output retained only on server')
    return result.stdout

def kube(*args, timeout=180, input_text=None):
    return command(['/usr/local/bin/kubectl', '--request-timeout=30s', *args], timeout, input_text)

def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()

def deployment(name, expected_image=None, replacement=None):
    d = json.loads(kube('get', 'deployment', name, '-n', 'shopixy-app', '-o', 'json'))
    spec = d['spec']; index = next(i for i,c in enumerate(spec['template']['spec']['containers']) if c['name'] in ('api','web','storefront'))
    if expected_image is not None: assert spec['template']['spec']['containers'][index]['image'] == expected_image
    expected = copy.deepcopy(spec)
    if replacement: expected['template']['spec']['containers'][index]['image'] = replacement
    selector = ','.join(k+'='+v for k,v in spec['selector']['matchLabels'].items())
    pods = [p for p in json.loads(kube('get','pods','-n','shopixy-app','-l',selector,'-o','json'))['items'] if not p['metadata'].get('deletionTimestamp')]
    return {'name': name, 'uid': d['metadata']['uid'], 'resourceVersion': d['metadata']['resourceVersion'],
        'generation': d['metadata']['generation'], 'observedGeneration': d.get('status',{}).get('observedGeneration'),
        'index': index, 'image': spec['template']['spec']['containers'][index]['image'], 'specSha256': digest(spec),
        'expectedImageOnlySpecSha256': digest(expected), 'replicas': spec.get('replicas'),
        **{k:d.get('status',{}).get(k,0) for k in ('readyReplicas','updatedReplicas','availableReplicas')},
        'pods':[{'name':p['metadata']['name'],'containers':[{k:c.get(k) for k in ('name','imageID','ready','restartCount')}
            for c in p.get('status',{}).get('containerStatuses',[])]} for p in pods]}

def ready(s):
    return s['replicas'] == s['readyReplicas'] == s['updatedReplicas'] == s['availableReplicas'] == 1 and \
        s['generation'] == s['observedGeneration'] and len(s['pods']) == 1 and all(c['ready'] for c in s['pods'][0]['containers'])

def import_image(archive, tag):
    command(['/usr/local/bin/k3s','ctr','images','import','--digests',str(archive)], 600)
    listing = command(['/usr/local/bin/k3s','ctr','images','list'])
    line = next(line for line in listing.splitlines() if line.split()[0] == tag)
    manifest = next(field for field in line.split()[1:] if field.startswith('sha256:'))
    immutable = tag.split(':',1)[0] + '@' + manifest
    try: identity = json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',immutable]))['status']
    except RuntimeError:
        command(['/usr/local/bin/k3s','ctr','images','tag',tag,immutable])
        identity = json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',immutable]))['status']
    inspected = json.loads(command(['/usr/local/bin/k3s','crictl','inspecti',tag]))
    labels = inspected.get('info',{}).get('imageSpec',{}).get('config',{}).get('Labels',{})
    assert labels.get('shopixy.io/source-input-sha256') == SOURCE
    assert identity['id'] == inspected['status']['id']
    return immutable, identity['id']

def patch_image(before, replacement):
    current = deployment(before['name'])
    assert current['uid'] == before['uid'] and current['specSha256'] == before['specSha256'], 'Deployment spec changed independently'
    path = '/spec/template/spec/containers/' + str(before['index']) + '/image'
    patch = [{'op':'test','path':'/metadata/uid','value':before['uid']},
        {'op':'test','path':'/metadata/resourceVersion','value':current['resourceVersion']},
        {'op':'test','path':path,'value':before['image']},{'op':'replace','path':path,'value':replacement}]
    args = ['patch','deployment',before['name'],'-n','shopixy-app','--type=json','-p',json.dumps(patch)]
    kube(*args,'--dry-run=server'); kube(*args)
    kube('rollout','status','deployment/'+before['name'],'-n','shopixy-app','--timeout=480s',timeout=510)

def rollback(name, new_image, old_image):
    try:
        count = kube('exec','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-qAt','-d','app','-c',
            'SELECT count(*) FROM "AffiliateManualPayoutReturns"').strip()
        if count != '0': return # Never hide committed compensation with a pre-return API.
        current = deployment(name)
        if current['image'] != new_image: return
        patch_image(current, old_image)
    except Exception:
        pass

metadata_recovery = sys.argv[1:] == ['--recover-schema-metadata-and-rollout']
resume = sys.argv[1:] == ['--resume-schema-verified'] or metadata_recovery
if resume:
    original_receipt = json.loads((ROOT/'release-started.json').read_text())
    assert original_receipt['sourceIndexSha256'] == SOURCE and original_receipt['archives'] == {'api':API_SHA,'web':WEB_SHA}
    if metadata_recovery:
        assert (ROOT/'release-failed.json').exists(), 'Wait for the previous rollout/rollback to finish'
        assert not (ROOT/'metadata-recovery-started.json').exists(), 'Existing recovery: inspect state, do not retry blindly'
    else:
        assert not (ROOT/'resume-started.json').exists(), 'Existing resume: inspect state, do not retry blindly'
else:
    assert not (ROOT/'release-started.json').exists(), 'Existing attempt: inspect state, do not retry blindly'
assert hashlib.sha256(API_ARCHIVE.read_bytes()).hexdigest() == API_SHA
assert hashlib.sha256(WEB_ARCHIVE.read_bytes()).hexdigest() == WEB_SHA
assert len(json.loads(kube('get','nodes','-o','json'))['items']) == 1
api_ref = 'docker.io/shopixy/api@sha256:880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3'
web_ref = 'docker.io/shopixy/web@sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991'
before_api = deployment('shopixy-api', OLD_API, api_ref); before_web = deployment('shopixy-web', OLD_WEB, web_ref)
before_storefront = deployment('shopixy-storefront', STOREFRONT)
assert (ready(before_api) or metadata_recovery) and ready(before_web) and ready(before_storefront)
api_ref, api_id = import_image(API_ARCHIVE, API_TAG)
web_ref, web_id = import_image(WEB_ARCHIVE, WEB_TAG)
receipt = {'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'sourceIndexSha256':SOURCE,
    'before':{'api':before_api,'web':before_web,'storefront':before_storefront},'intended':{'api':api_ref,'web':web_ref},
    'archives':{'api':API_SHA,'web':WEB_SHA},'schema':'additive-two-table-audit-migration','businessDataWrites':False,
    'operationalSchemaVersionMetadataCorrected':metadata_recovery,'recoveryStartedFromUnreadyApi':not ready(before_api)}
(ROOT/('metadata-recovery-started.json' if metadata_recovery else 'resume-started.json' if resume else 'release-started.json')).write_text(json.dumps(receipt,indent=2))
if not resume:
    sql = "SET lock_timeout='5s'; SET statement_timeout='60s'; SET ROLE app;\n" + (ROOT/'schema.sql').read_text(encoding='utf-8-sig')
    kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-d','app','-v','ON_ERROR_STOP=1',input_text=sql)
    kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-d','app','-v','ON_ERROR_STOP=1',
        input_text=(ROOT/'grant.sql').read_text())
    kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-d','app','-v','ON_ERROR_STOP=1',
        input_text=(ROOT/'schema-version.sql').read_text(encoding='utf-8-sig'))
verify = kube('exec','-i','-n','shopixy-data','shopixy-postgres-1','-c','postgres','--','psql','-X','-d','app','-qAt','-v','ON_ERROR_STOP=1',
    input_text=(ROOT/'verify.sql').read_text()).strip().splitlines()
assert verify == ['t','t','t','t','t','t','0','0']
api_patched = web_patched = False
try:
    api_patched = True; patch_image(before_api, api_ref)
    web_patched = True; patch_image(before_web, web_ref)
    after_api = deployment('shopixy-api'); after_web = deployment('shopixy-web'); after_storefront = deployment('shopixy-storefront')
    assert ready(after_api) and ready(after_web) and ready(after_storefront)
    assert after_api['specSha256'] == before_api['expectedImageOnlySpecSha256']
    assert after_web['specSha256'] == before_web['expectedImageOnlySpecSha256']
    assert after_storefront['specSha256'] == before_storefront['specSha256']
    receipt.update({'after':{'api':after_api,'web':after_web,'storefront':after_storefront},
        'runtimeConfigIds':{'api':api_id,'web':web_id},'schemaVerified':True,'runtimeGrantVerified':True,
        'completedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()})
    (ROOT/'release-verified.json').write_text(json.dumps(receipt,indent=2))
except Exception as error:
    if web_patched: rollback('shopixy-web',web_ref,OLD_WEB)
    if api_patched: rollback('shopixy-api',api_ref,OLD_API)
    receipt.update({'failedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'failureClass':type(error).__name__,'imagesRolledBackBestEffort':True,
        'schemaRetained':True,'grantRetained':True})
    (ROOT/'release-failed.json').write_text(json.dumps(receipt,indent=2))
    raise
print(json.dumps(receipt,indent=2))
