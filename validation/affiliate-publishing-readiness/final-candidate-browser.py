"""Bounded final-image admission. Never submits checkout or modifies financial history."""
import importlib.util
import json
from pathlib import Path

out = Path(__file__).parent
spec = importlib.util.spec_from_file_location('steps',out/'run-DE.py')
# Load only the runner helper; importing the scenario loop would repeat tests.
namespace = {'__file__':str(out/'run-DE.py'),'__name__':'reviewed_steps'}
source = (out/'run-DE.py').read_text().split('for scenario in sys.argv[1:]')[0]
exec(compile(source,str(out/'run-DE.py'),'exec'),namespace)
run = namespace['run']
run('owner','scenario-A','final-candidate-A')
run('affiliate','affiliate-restored','final-candidate-affiliate-ready')
run('owner','scenario-G-disable','final-candidate-G')
try:
    run('affiliate','affiliate-blocked','final-candidate-bypass')
    run('owner','scenario-K','final-candidate-K')
finally:
    run('owner','scenario-G-restore','final-candidate-restoration')
run('affiliate','affiliate-restored','final-candidate-affiliate-restored')
(out/'final-candidate-browser.json').write_text(json.dumps({'passed':True,'headed':True,'onlyIsolatedFixture':True,
    'checks':['four ready sections','eligible catalog and existing link','simultaneous blockers','API stale/cross-tenant bypass','English/Arabic navigation','partial and complete restoration'],
    'financialWorkflowRepeated':False,'productionBusinessMutations':False},indent=2)+'\n')
print('Final immutable API/Web candidates passed bounded headed readiness acceptance; isolated prerequisites restored.')
