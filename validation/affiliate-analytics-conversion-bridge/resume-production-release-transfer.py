"""Resume the sealed release transfer in bounded chunks after the initial SSH pipe failure."""
import hashlib,json,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2];staging=max(root.parent.glob('analytics-production-release*'),key=lambda p:p.stat().st_mtime);remote=(staging/'remote-directory.txt').read_text().strip()
expected=json.loads((staging/'SHA256SUMS.json').read_text())
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','test','-d',remote],check=True,timeout=120)
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','test','!','-e',remote+'/release-started.json'],check=True,timeout=120)
# Exact incomplete temp file was created by this release's failed first scp.
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','rm','-f','/tmp/api.tar'],check=True,timeout=120)
subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','mkdir','-p',remote+'/chunks'],check=True,timeout=120)
chunk_dir=staging/'chunks';chunk_dir.mkdir(exist_ok=True);chunk_size=32*1024*1024
for source in sorted(f for f in staging.iterdir() if f.is_file()):
    source_expected=expected.get(source.name,hashlib.sha256(source.read_bytes()).hexdigest())
    target=remote+'/'+source.name
    current=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','sha256sum',target],capture_output=True,text=True,timeout=120)
    if current.returncode==0 and current.stdout.split()[0]==source_expected: continue
    parts=[]
    with source.open('rb') as stream:
        index=0
        while data:=stream.read(chunk_size):
            part=chunk_dir/f'{source.name}.part{index:04d}';index+=1
            if not part.exists(): part.write_bytes(data)
            assert part.read_bytes()==data;parts.append(part)
    for part in parts:
        digest=hashlib.sha256(part.read_bytes()).hexdigest();remote_part=remote+'/chunks/'+part.name
        prior=subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','sha256sum',remote_part],capture_output=True,text=True,timeout=120)
        if prior.returncode==0 and prior.stdout.split()[0]==digest: continue
        temp='/tmp/'+part.name
        subprocess.run(['scp','-q','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=20',str(part),'shopixy-vps:'+temp],check=True,timeout=600)
        subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','mv',temp,remote_part],check=True,timeout=120)
        check=subprocess.check_output(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','sha256sum',remote_part],text=True,timeout=120).split()[0]
        assert check==digest
    remote_parts=[remote+'/chunks/'+p.name for p in parts]
    concatenate="import shutil,sys\nwith open(sys.argv[1],'wb') as out:\n for name in sys.argv[2:]:\n  with open(name,'rb') as src: shutil.copyfileobj(src,out)\n"
    subprocess.run(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','python3','-',target,*remote_parts],input=concatenate,text=True,check=True,timeout=600)
    actual=subprocess.check_output(['ssh','-o','BatchMode=yes','shopixy-vps','sudo','sha256sum',target],text=True,timeout=600).split()[0]
    assert actual==source_expected,source.name
print(json.dumps({'remoteDirectory':remote,'filesTransferred':len([f for f in staging.iterdir() if f.is_file()]),'allRemoteSha256Verified':True,'deployed':False},indent=2))
