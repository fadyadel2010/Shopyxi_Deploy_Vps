import importlib.util,json
from pathlib import Path
out=Path(__file__).parent
spec=importlib.util.spec_from_file_location('local',out/'local-tools.py');local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)
local.assert_isolated()
sql='''BEGIN READ ONLY;
SELECT json_build_object('slug',p."Slug",'code',l."ReferralCode",'productId',p."Id",'stock',s."Quantity"-s."ReservedQuantity",
 'linkActive',l."IsActive",'productActive',p."IsActive")
FROM "Products" p JOIN "AffiliatePromotionLinks" l ON l."ProductId"=p."Id"
LEFT JOIN "ProductVariants" v ON v."ProductId"=p."Id" AND v."IsDefault"
LEFT JOIN "ProductVariantStocks" s ON s."VariantId"=v."Id"
WHERE l."Id"='01a1dfa0-8334-755b-aeb8-c037243e70c0';
ROLLBACK;'''
data=json.loads(local.query(sql));print(json.dumps(data,indent=2))
