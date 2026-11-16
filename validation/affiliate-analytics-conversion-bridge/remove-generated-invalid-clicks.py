"""Remove only two isolated click records minted by the discovered pre-fix post-order bug."""
import importlib.util,json
from pathlib import Path
out=Path(__file__).parent
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local);local.assert_isolated()
ids=['64e73a16-57e3-4d0f-9185-c73a976c99c1','e5850b2a-f8a8-4146-a3b6-06b25761cdd2']
js="""const d=db.getSiblingDB('affiliate_full_e2e');
const ids=%s;const docs=ids.map(id=>d.affiliate_clicks.findOne({_id:id})).filter(Boolean);
if(docs.length!==2||docs.some(x=>x.tenantId!=='15150000-2026-4015-a000-000000000001'||x.orderId!=null||x.convertedAtUtc!=null))throw new Error('bounded cleanup authority mismatch');
const deleted=ids.map(id=>d.affiliate_clicks.deleteOne({_id:id,tenantId:'15150000-2026-4015-a000-000000000001',orderId:null,convertedAtUtc:null}).deletedCount).reduce((a,b)=>a+b,0);
if(deleted!==2)throw new Error('bounded cleanup count mismatch');
print(JSON.stringify({deletedGeneratedInvalidClicks:deleted,conversionDocuments:d.affiliate_order_conversions.countDocuments({}),originalClickRetained:d.affiliate_clicks.countDocuments({_id:'3de85e14-3e61-4b8d-a964-a49aeb3f1ba7'})===1}));""" % json.dumps(ids)
r=local.docker('exec','affiliate-full-mongo','mongosh','--quiet','--eval',js)
data=json.loads(r.stdout);assert data=={'deletedGeneratedInvalidClicks':2,'conversionDocuments':2,'originalClickRetained':True}
target=out/'generated-invalid-click-cleanup.json';assert not target.exists();target.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
