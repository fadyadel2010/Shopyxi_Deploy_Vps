"""Fingerprint preexisting isolated Mongo click documents without exporting their bodies."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('stack',root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec); spec.loader.exec_module(stack)
stage = sys.argv[1]; assert stage in ('before','after')
private = root.parent/'publishing-readiness-clicks-review-private.json'
assert stage == 'after' or not private.exists()
result = stack.docker('exec','affiliate-full-mongo','mongosh','--quiet','--eval',
    "print(EJSON.stringify(db.getSiblingDB('affiliate_full_e2e').affiliate_clicks.find({}).sort({_id:1}).toArray()))")
rows = json.loads(result.stdout)
fingerprints = {json.dumps(row['_id'],sort_keys=True):hashlib.sha256(json.dumps(row,sort_keys=True).encode()).hexdigest() for row in rows}
if stage == 'before': private.write_text(json.dumps(fingerprints))
else:
    prior = json.loads(private.read_text())
    assert all(fingerprints.get(key) == value for key,value in prior.items()), 'Historical click changed or disappeared'
receipt = {'stage':stage,'existingHistoricalClickCount':len(fingerprints) if stage == 'before' else len(prior),
    'allExistingClicksUnchanged':True if stage == 'after' else None,'privateDocumentBodiesExported':False}
(Path(__file__).parent/('click-safety-review-'+stage+'.json')).write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
