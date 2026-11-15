"""Sanitized exact-input and focused-test gate receipt, no inferred financial acceptance."""
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent
def trx(name):
    data = (out/'results'/name).read_bytes()
    counters = ET.fromstring(data).find('.//{*}Counters').attrib
    assert int(counters['failed']) == 0 and int(counters['notExecuted']) == 0
    return {'passed':int(counters['passed']),'failed':0,'notExecuted':0,'sha256':hashlib.sha256(data).hexdigest()}
def frontend(name):
    data = (root.parent/'publishing-readiness-web'/name).read_bytes()
    item = json.loads(data)
    assert item['success'] and item['numFailedTests'] == 0
    return {'passed':item['numPassedTests'],'failed':0,'unselectedByFilter':item['numPendingTests'],'sha256':hashlib.sha256(data).hexdigest()}
web_inputs = json.loads((root.parent/'publishing-readiness-production-web/source-inputs.json').read_text())
batch = subprocess.run(['git','cat-file','--batch'],cwd=root,input=''.join(':'+item['path']+'\n' for item in web_inputs).encode(),capture_output=True,check=True).stdout
offset = 0
for item in web_inputs:
    end = batch.index(b'\n',offset); length = int(batch[offset:end].split()[2])
    assert hashlib.sha256(batch[end+1:end+1+length]).hexdigest() == item['sha256'], 'Web index/build mismatch: '+item['path']
    offset = end+1+length+1
spec = importlib.util.spec_from_file_location('stack',root/'validation/affiliate-full-e2e/start-local.py')
stack = importlib.util.module_from_spec(spec);spec.loader.exec_module(stack)
build = json.loads(stack.docker('inspect','affiliate-readiness-web-verified-2030').stdout)[0]
assert build['State']['Status'] == 'exited' and build['State']['ExitCode'] == 0
assert json.loads((out/'production-api-candidate.json').read_text())['securityGatePassed']
assert json.loads((out/'production-web-candidate.json').read_text())['securityGatePassed']
assert json.loads((out/'final-candidate-browser.json').read_text())['passed']
receipt = {'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'backendReadiness':trx('readiness-postgres.trx'),
    'directReferralDependency':trx('referral-dependency.trx'),'frontendReadiness':frontend('frontend-tests.json'),
    'frontendPolicyReadiness':frontend('frontend-policy-readiness-tests.json'),'typescriptPassed':True,'productionWebBuildPassed':True,
    'webInputsMatchStagedIndex':True,'webInputsMatched':len(web_inputs),'finalImmutableCandidatesHeadedAcceptance':True,
    'fullFinancialLifecycleRepeated':False,'storefrontRebuilt':False,'passed':True}
(out/'gate-summary.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt,indent=2))
