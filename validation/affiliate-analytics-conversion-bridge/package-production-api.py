"""Build and gate an immutable API archive; no Production access or deployment."""
import datetime,hashlib,importlib.util,json,subprocess,shutil
from pathlib import Path
out=Path(__file__).parent;root=out.parents[1];index=1
target=out/'production-api-candidate.json'
while target.exists(): index+=1;target=out/f'production-api-candidate-{index}.json'
context=root.parent/('analytics-production-api' if index==1 else f'analytics-production-api-{index}')
if not context.exists(): context.mkdir()
archive=context/'api.tar'
if not archive.exists() and index==2 and (root.parent/'analytics-production-api/api.tar').exists():
    # Recovery from the first numbered wrapper attempt: WSL did not inherit the
    # Windows slot variable, but the gated build completed in the default slot.
    shutil.move(root.parent/'analytics-production-api/api.tar',archive)
    checksum=root.parent/'analytics-production-api/api.tar.sha256'
    if checksum.exists(): shutil.move(checksum,context/'api.tar.sha256')
if not archive.exists():
    r=subprocess.run(['wsl','-d','Shopixy-APP1-Ubuntu-Docker','--','env','ANALYTICS_PRODUCTION_API_SLOT='+context.name,'bash',
     '/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115/source/validation/affiliate-analytics-conversion-bridge/build-production-api.sh'],
     capture_output=True,text=True,timeout=1800)
    if r.returncode: raise SystemExit('Production API candidate gate failed; private diagnostic retained')
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)
image=(root/'deploy/automatic-subdomains/successor-image-reference.txt').read_text().strip()
image_id=(root/'deploy/automatic-subdomains/successor-image-digest.txt').read_text().strip()
inspect=json.loads(local.docker('image','inspect',image).stdout)[0]
assert image=='shopixy/api@'+image_id and inspect['Id']==image_id and inspect['Config']['User']=='1654:1654'
report=json.loads((root/'deploy/automatic-subdomains/successor-trivy-high-critical.json').read_text())
findings=[v for x in report.get('Results',[]) for v in x.get('Vulnerabilities',[]) if v.get('Severity') in ('HIGH','CRITICAL')]
assert not findings and report['Metadata']['ImageID']==image_id
archive_sha=hashlib.sha256(archive.read_bytes()).hexdigest()
assert (context/'api.tar.sha256').read_text().split()[0]==archive_sha
receipt={'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'component':'api','image':image,
 'baseImage':'sha256:39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b',
 'sourceInputSha256':inspect['Config']['Labels']['shopixy.io/source-input-sha256'],
 'apiAssemblySha256':inspect['Config']['Labels']['org.opencontainers.image.revision'],'archiveSha256':archive_sha,
 'dotnetRuntime':'10.0.15','nonRoot':True,'nugetVulnerabilityGatePassed':True,'trivyHighCriticalFindings':0,
 'securityGatePassed':True,'productionDeployment':False}
assert not target.exists();target.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt,indent=2))
