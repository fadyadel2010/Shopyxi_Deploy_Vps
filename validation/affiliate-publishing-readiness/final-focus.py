"""Verify same-page cache retirement on the final admitted images; restore the sole COD method."""
from pathlib import Path

out=Path(__file__).parent
namespace={'__file__':str(out/'run-DE.py'),'__name__':'reviewed_steps'}
exec(compile((out/'run-DE.py').read_text().split('for scenario in sys.argv[1:]')[0],str(out/'run-DE.py'),'exec'),namespace)
run=namespace['run']
run('affiliate','affiliate-restored','final-focus-initial-ready')
run('owner','scenario-D-disable','final-focus-payment-disabled')
try:run('affiliate','catalog-focus-blocked','catalog-focus-blocked-final')
finally:run('owner','scenario-D-restore','final-focus-payment-restored')
run('affiliate','catalog-focus-restored','catalog-focus-restored-final')
print('Final-image same-page eligibility withdrawal/restoration verified; COD restored.')
