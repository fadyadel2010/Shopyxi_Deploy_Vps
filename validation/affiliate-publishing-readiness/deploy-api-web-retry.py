"""One bounded retry after verified rollback; preserve and reuse the sealed original guard."""
import ast
import tarfile

ORIGINAL = '/var/lib/shopixy/affiliate-publishing-readiness-20261115'
RETRY = ORIGINAL+'-retry1'
from pathlib import Path
import hashlib
import json

inputs_retry=json.loads((Path(RETRY)/'release-input.json').read_text())
original_source=(Path(ORIGINAL)/'deploy-api-web.py').read_bytes()
assert hashlib.sha256(original_source).hexdigest()==inputs_retry['originalDeploymentScriptSha256']
failed=json.loads((Path(ORIGINAL)/'release-failed.json').read_text())
assert failed['rollbackVerified']=={'api':True} and not (Path(ORIGINAL)/'release-verified.json').exists()
source=original_source.decode()
prefix,suffix=source.split("assert not (ROOT/'release-started.json').exists()",1)
# The original prefix contains imports, constants and functions only; no release side effects.
assert all(isinstance(node,(ast.Import,ast.ImportFrom,ast.Assign,ast.FunctionDef,ast.Expr)) for node in ast.parse(prefix).body)
exec(compile(prefix,'sealed-original-guard','exec'),globals())
ROOT=Path(RETRY)
GENERATIONS={'api':52,'web':15,'storefront':10}
original_admission=image_admission

def image_admission(kind,item):
    archive=ROOT/(kind+'.tar.gz')
    assert sha(archive.read_bytes())==item['archiveSha256']
    with tarfile.open(archive,'r:gz') as tar:
        index_bytes=tar.extractfile('index.json').read()
    wrapper='sha256:'+sha(index_bytes)
    node=json.loads(index_bytes)
    assert len(node['manifests'])==1 and node['manifests'][0]['digest']==item['image'].split('@')[1]
    # --digests alone discards the outer archive index. CRI can retain its normalized
    # name for the identical config ID; preserve that exact wrapper, never mis-tag it.
    wrapper_alias='docker.io/library/import-2026-11-15@'+wrapper
    call('/usr/local/bin/k3s','ctr','images','import','--digests','--index-name',wrapper_alias,str(archive),timeout=600)
    admitted=original_admission(kind,item)
    inspection=json.loads(call('/usr/local/bin/k3s','crictl','inspecti',item['image']))
    assert inspection['status']['id']==item['runtimeConfigId']
    refs={row.split()[0]:row.split()[2] for row in call('/usr/local/bin/k3s','ctr','images','list').splitlines()[1:] if row.split()}
    checked=[]
    for ref in inspection['status']['repoDigests']:
        digest=ref.split('@')[1]
        assert refs.get(ref)==digest,'CRI cached reference missing or misdirected; stop before rollout'
        selected=[m for m in manifests(digest) if m.get('config',{}).get('digest')==item['runtimeConfigId']]
        canonical=[m for m in manifests(item['image'].split('@')[1]) if m.get('config',{}).get('digest')==item['runtimeConfigId']]
        assert len(selected)==len(canonical)==1 and selected[0]['layers']==canonical[0]['layers']
        checked.append(ref)
    admitted.update({'archiveWrapperAliasPreserved':True,'allCriRepoDigestsResolveToExactCandidate':True,'verifiedCriRepoDigests':checked})
    return admitted

exec(compile("assert not (ROOT/'release-started.json').exists()"+suffix,'sealed-original-release-body','exec'),globals())
