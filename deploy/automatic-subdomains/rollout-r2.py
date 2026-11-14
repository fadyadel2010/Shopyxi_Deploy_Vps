"""Versioned operator runner. Executes one reviewed RUNBOOK stage at a time.

Run on the node as the approved privileged operator. Credentials stay in their
protected file or Kubernetes; output is restricted to non-secret receipts.
"""
import base64
import datetime
import gzip
import hashlib
import json
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent
IMAGE = 'docker.io/shopixy/api@sha256:1185031a83553260e30f4acc63726469446341eb6c9e00452e102cd8817444aa'
OLD = 'docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e'
ARCHIVE = 'shopixy-api-automatic-subdomains-audit-r2-1185031a8355.tar.gz'
ARCHIVE_SHA = '8d787512f680759954f811a4e0f78d5f051efb01633df158c6f10a6b0512eb52'
stage = sys.argv[1]
receipts = []


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def run(args, data=None, timeout=90):
    p = subprocess.run(args, input=data, capture_output=True, text=True, timeout=timeout)
    if p.returncode:
        raise RuntimeError('Stage command failed: ' + ' '.join(args[:5]) + '; ' + p.stderr[:1000])
    return p.stdout


def k(*args, data=None, timeout=90):
    return run(['kubectl', '--request-timeout=30s', *args], data, timeout)


def get(kind, name, namespace):
    return json.loads(k('get', kind, name, '-n', namespace, '-o', 'json'))


def apply(filename):
    k('apply', '--dry-run=server', '-f', str(ROOT / filename))
    result = k('apply', '-f', str(ROOT / filename))
    receipts.append({'apply': filename, 'result': result.strip()})


def patch(kind, name, namespace, filename):
    k('patch', kind, name, '-n', namespace, '--type=json', '--patch-file=' + str(ROOT / filename), '--dry-run=server')
    result = k('patch', kind, name, '-n', namespace, '--type=json', '--patch-file=' + str(ROOT / filename))
    receipts.append({'patch': filename, 'resource': namespace + '/' + name, 'result': result.strip()})


def rollout(name, namespace):
    k('rollout', 'status', 'deployment/' + name, '-n', namespace, '--timeout=300s', timeout=330)
    d = get('deployment', name, namespace)
    assert d['spec']['replicas'] == 1 and d['spec']['strategy']['type'] == 'Recreate'
    assert d['status'].get('readyReplicas') == 1 and d['status'].get('updatedReplicas') == 1
    receipts.append({'readySingletonRecreate': namespace + '/' + name})


def sql(filename):
    query = (ROOT / filename).read_text()
    if filename.endswith('AutomatedTenantProvisioning.sql'):
        assert hashlib.sha256((ROOT / filename).read_bytes()).hexdigest() == 'b6083cefea5b6e5257dc020a9762d6a20ccc8fab5f3cd3adc8f649b0023489b7'
    output = k('exec', '-i', '-n', 'shopixy-data', 'shopixy-postgres-1', '-c', 'postgres', '--',
               'env', 'PGOPTIONS=-c lock_timeout=5s -c statement_timeout=120s',
               'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', 'app', data=query, timeout=150)
    receipts.append({'sqlFile': filename, 'result': output})


def verify_seals():
    for filename in ('SHA256SUMS.txt', 'audit-results-SHA256SUMS.txt'):
        for line in (ROOT / filename).read_text().splitlines():
            expected, name = line.split('  ', 1)
            path = ROOT / name
            assert path.is_file() and not path.is_symlink() and ROOT in path.resolve().parents
            assert file_sha256(path) == expected, name


def ready_cert(name):
    k('wait', '-n', 'shopixy-app', '--for=condition=Ready', 'certificate/' + name, '--timeout=10m', timeout=630)
    c = get('certificate', name, 'shopixy-app')
    assert any(x['type'] == 'Ready' and x['status'] == 'True' for x in c['status']['conditions'])
    receipts.append({'certificate': name, 'status': c['status']})


try:
    assert os.geteuid() == 0, 'Use the existing approved privileged operator'
    verify_seals()
    if stage == 'import':
        for name in ('shopixy-api', 'shopixy-storefront'):
            d = get('deployment', name, 'shopixy-app')
            assert d['spec']['replicas'] == 1 and d['spec']['strategy']['type'] == 'Recreate'
        assert get('deployment', 'shopixy-api', 'shopixy-app')['spec']['template']['spec']['containers'][0]['image'] == OLD
        sql('preflight.sql')
        inventory = run(['k3s', 'ctr', 'images', 'list'])
        assert OLD in inventory, 'Accepted rollback image absent from node'
        assert file_sha256(ROOT / ARCHIVE) == ARCHIVE_SHA
        # Stream decompression to containerd without replacing an existing tag.
        compressed = subprocess.Popen(['gzip', '-dc', str(ROOT / ARCHIVE)], stdout=subprocess.PIPE)
        imported = subprocess.run(['k3s', 'ctr', 'images', 'import', '--digests', '-'], stdin=compressed.stdout, capture_output=True, text=True, timeout=300)
        compressed.stdout.close()
        assert compressed.wait(timeout=30) == 0 and imported.returncode == 0, 'Containerd import failed'
        inventory = run(['k3s', 'ctr', 'images', 'list'])
        matches = [line for line in inventory.splitlines() if 'automatic-subdomains-ce7fa5ea422e-r2' in line or IMAGE in line]
        assert any(IMAGE.split('@')[1] in line and 'automatic-subdomains-ce7fa5ea422e-r2' in line for line in matches)
        # Docker archive import preserves the tag but containerd may synthesize an
        # import repoDigest. Bind the reviewed manifest digest explicitly so
        # imagePullPolicy Never can resolve the deployment's immutable reference.
        run(['k3s', 'ctr', 'images', 'tag',
             'docker.io/shopixy/api:automatic-subdomains-ce7fa5ea422e-r2', IMAGE])
        cri = json.loads(run(['k3s', 'crictl', 'inspecti', '--output', 'json', IMAGE]))
        assert IMAGE in cri['status']['repoDigests'] and cri['status']['uid']['value'] == '1654'
        receipts.append({'importedImage': IMAGE, 'inventoryMatches': matches,
                         'criDigestResolution': True, 'runtimeUid': '1654', 'workloadsChanged': False})
    elif stage == 'certificates':
        operator_script = '\n'.join([
            'set -euo pipefail',
            'kubectl() { sudo -n /usr/local/bin/kubectl "$@"; }',
            'export -f kubectl',
            'bash ' + str(ROOT / 'install-cloudflare-token-secret.sh') + ' /home/shopixy-admin/.config/shopixy/secrets/cloudflare-dns01-token'])
        run(['sudo', '-n', '-u', 'shopixy-admin', 'bash'], operator_script)
        receipts.append({'dns01SecretInstalled': 'cert-manager/cloudflare-dns01-token', 'credentialPrinted': False})
        apply('01-cert-manager-cloudflare-issuers.yaml')
        apply('02a-wildcard-certificate-staging.yaml')
        ready_cert('shopixy-wildcard-origin-staging-r1')
        apply('02-wildcard-certificate.yaml')
        ready_cert('shopixy-wildcard-origin-r1')
        secret = get('secret', 'shopixy-wildcard-origin-r1-tls', 'shopixy-app')
        public_cert = base64.b64decode(secret['data']['tls.crt']).decode()
        details = run(['openssl', 'x509', '-noout', '-subject', '-issuer', '-dates', '-ext', 'subjectAltName', '-fingerprint', '-sha256'], public_cert)
        assert 'DNS:*.shopyxi.com' in details and 'DNS:shopyxi.com' in details
        assert 'STAGING' not in details.upper() and 'FAKE' not in details.upper()
        run(['openssl', 'x509', '-noout', '-checkend', '2592000'], public_cert)
        receipts.append({'productionCertificatePublicDetails': details, 'privateKeyExported': False})
        k('delete', 'certificate', 'shopixy-wildcard-origin-staging-r1', '-n', 'shopixy-app')
        k('delete', 'secret', 'shopixy-wildcard-origin-staging-r1-tls', '-n', 'shopixy-app', '--ignore-not-found=true')
    elif stage == 'migrate':
        ready_cert('shopixy-wildcard-origin-r1')
        b = get('backup', 'automatic-subdomains-preflight-r2-20261111', 'shopixy-data')
        assert b['status']['phase'] == 'completed'
        age = datetime.datetime.now(datetime.timezone.utc) - datetime.datetime.fromisoformat(b['status']['stoppedAt'].replace('Z', '+00:00'))
        assert age.total_seconds() < 7200, 'Fresh recovery point expired; create and review another backup'
        sql('preflight.sql')
        sql('20261111160000_AutomatedTenantProvisioning.sql')
        sql('12-provisioning-runtime-grants.sql')
        sql('13-schema-version-authority.sql')
        sql('postflight.sql')
    elif stage == 'configure':
        sql('postflight.sql')
        for filename in ('03-storefront-nginx-configmap.yaml', '04-api-edge-nginx-configmap.yaml', '08-monitoring-combined.yaml'):
            apply(filename)
        patch('deployment', 'shopixy-grafana', 'monitoring', '08-monitoring-deployment-patch.json')
        rollout('shopixy-grafana', 'monitoring')
    elif stage == 'rollout':
        before = {name: get('deployment', name, 'shopixy-app') for name in ('shopixy-api', 'shopixy-storefront')}
        patch('deployment', 'shopixy-api', 'shopixy-app', '07-api-edge-deployment-patch.json')
        rollout('shopixy-api', 'shopixy-app')
        patch('deployment', 'shopixy-storefront', 'shopixy-app', '06-storefront-deployment-patch.json')
        rollout('shopixy-storefront', 'shopixy-app')
        for name, old in before.items():
            after = get('deployment', name, 'shopixy-app')
            old_containers = old['spec']['template']['spec']['containers']
            new_containers = after['spec']['template']['spec']['containers']
            assert len(old_containers) == len(new_containers)
            for a, b in zip(old_containers, new_containers):
                assert a.get('resources') == b.get('resources')
                if name != 'shopixy-api' or a['name'] != 'api':
                    assert a['image'] == b['image']
        assert get('deployment', 'shopixy-api', 'shopixy-app')['spec']['template']['spec']['containers'][0]['image'] == IMAGE
        closed = k('exec', '-n', 'shopixy-app', 'deployment/shopixy-api', '-c', 'api', '--', 'printenv', 'PlatformSecurity__RegistrationEnabled')
        assert closed.strip() == 'false'
        receipts.append({'resourcePolicyUnchanged': True, 'frontendImagesUnchanged': True, 'registrationClosed': True})
    elif stage == 'routes':
        ready_cert('shopixy-wildcard-origin-r1')
        apply('05-traefik-wildcard-routes.yaml')
    elif stage == 'acceptance':
        result = run(['bash', str(ROOT / 'acceptance-probes.sh')], timeout=600)
        receipts.append({'acceptanceProbes': result})
    elif stage == 'retire-k8s':
        assert (ROOT / 'live-receipts/acceptance.json').is_file(), 'Pre-retirement acceptance receipt required'
        patch('ingressroute', 'controlled-qa-registration-private-r1', 'shopixy-app', '11-retire-ahram-private-route-patch.json')
        for name in ('controlled-qa-ahram-http-r1', 'controlled-qa-ahram-https-r1'):
            route = get('ingressroute', name, 'shopixy-app')
            assert route['metadata']['labels']['shopixy.io/tenant-id'] == 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0'
            k('delete', 'ingressroute', name, '-n', 'shopixy-app')
        certificate = get('certificate', 'controlled-qa-ahram-r1', 'shopixy-app')
        assert certificate['spec']['dnsNames'] == ['ahram.shopyxi.com']
        k('delete', 'certificate', 'controlled-qa-ahram-r1', '-n', 'shopixy-app')
        receipts.append({'retiredLegacyAhramRoutesAndCertificate': True, 'legacyTlsSecretRetained': True, 'dnsNotChanged': True})
    else:
        raise ValueError('Unknown stage')
    receipt = {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'stage': stage, 'pass': True, 'receipts': receipts, 'secretsPrinted': False}
except Exception as error:
    receipt = {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'stage': stage, 'pass': False, 'receipts': receipts, 'error': str(error), 'secretsPrinted': False}
    # Recreate failure must not leave the singleton API in an unaccepted state.
    if stage == 'rollout':
        try:
            for name, filename, marker in (
                ('shopixy-storefront', '09-rollback-storefront-deployment-patch.json', 'shopixy-storefront-wildcard-r1'),
                ('shopixy-api', '10-rollback-api-edge-deployment-patch.json', IMAGE)):
                d = get('deployment', name, 'shopixy-app')
                if marker in json.dumps(d['spec']):
                    patch('deployment', name, 'shopixy-app', filename)
                    rollout(name, 'shopixy-app')
            receipt['applicationRollback'] = 'Accepted application/configuration restored; additive migration retained'
        except Exception as rollback_error:
            receipt['applicationRollbackError'] = str(rollback_error)
finally:
    target = ROOT / 'live-receipts'
    target.mkdir(exist_ok=True)
    (target / (stage + '.json')).write_text(json.dumps(receipt, indent=2))
    print(json.dumps(receipt, indent=2))
if not receipt['pass']:
    raise SystemExit(1)
