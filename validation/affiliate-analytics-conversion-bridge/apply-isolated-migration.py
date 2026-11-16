"""Apply only the reviewed migration to the dedicated internal fixture database."""
import importlib.util,json
from pathlib import Path
out=Path(__file__).parent;root=out.parents[1]
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
api=json.loads(local.docker('inspect','affiliate-full-api').stdout)[0]
env=dict(x.split('=',1) for x in api['Config']['Env'] if '=' in x)
connection=env['ConnectionStrings__DefaultConnection']
index=1
private=root.parent/'analytics-migration-private.env'
while private.exists():
    index+=1;private=root.parent/f'analytics-migration-private-{index}.env'
private.write_text('ConnectionStrings__DefaultConnection='+connection+'\n')
args=['run','--rm','--network',local.NETWORK,'--pull=never','--entrypoint','/bin/bash',
 '-v',local.WSL_BASE+'/source:/src','-v','/home/fadya/.dotnet10-10.0.404:/dotnet:ro',
 '-v','/home/fadya/.nuget/packages:/packages:ro','--env-file',local.WSL_BASE+'/'+private.name,
 'postgres:18.6','/src/validation/affiliate-analytics-conversion-bridge/apply-isolated-migration.sh']
r=local.docker(*args,check=False,timeout=300)
log=root.parent/('analytics-migration-private.log' if index==1 else f'analytics-migration-private-{index}.log')
log.write_text(r.stdout+r.stderr)
if r.returncode: raise SystemExit('Isolated migration failed; private diagnostic retained')
check=local.query('''BEGIN READ ONLY; SELECT count(*) FROM information_schema.columns WHERE table_name='AffiliateAttributions' AND column_name IN ('ReferralClickId','ReferralProductId'); ROLLBACK;''')
assert check=='2'
print('Reviewed Affiliate Analytics migration applied to isolated fixture only; two columns verified read-only.')
