"""Strip embedded outputs from boundary_prototype_v2.ipynb before committing.

run_notebook.py writes rendered figures back into the notebook so the operator
sees the plots in Jupyter, which takes it to ~30MB.  That is right for working
on it and wrong for git history.  This strips outputs in place; run it before a
commit, then re-run run_notebook.py to get the pictures back.
"""
import json, os, sys

NB = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                  'boundary_prototype_v2.ipynb')
nb = json.load(open(NB))
n = 0
for c in nb['cells']:
    if c.get('cell_type') == 'code':
        n += len(c.get('outputs', []))
        c['outputs'] = []
        c['execution_count'] = None
json.dump(nb, open(NB, 'w'), indent=1)
print(f'stripped {n} outputs -> {os.path.getsize(NB)/1e6:.2f}MB')
