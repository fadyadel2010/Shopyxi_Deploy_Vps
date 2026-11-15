"""Headed readiness-only sequence. Always restore the isolated prerequisite after observation."""
import json
from pathlib import Path
import subprocess
import sys

out = Path(__file__).parent
def run(session, name, label=None):
    result = subprocess.run([sys.executable,str(out/'browser-run.py'),'affiliate-full-'+session,name,label or name],capture_output=True,text=True,encoding='utf-8',timeout=180)
    if result.returncode:
        raise RuntimeError('Headed step incomplete: '+name+'; inspect its private diagnostic, do not repeat blindly')
    return json.loads(result.stdout)

for scenario in sys.argv[1:] or ('D','E'):
    assert scenario in ('D','E','I-product')
    run('owner','scenario-'+scenario+('' if scenario == 'I-product' else '-disable'))
    try:
        run('affiliate','affiliate-blocked','scenario-'+scenario+'-affiliate-blocked')
        run('customer','referral-blocked','scenario-'+scenario+'-referral-blocked')
    finally:
        restored = run('owner','scenario-'+scenario+'-restore')
    run('affiliate','affiliate-restored','scenario-'+scenario+'-affiliate-restored')
    print(json.dumps({'scenario':scenario,'headedStepsCompleted':5,'isolatedPrerequisiteRestored':True}))
