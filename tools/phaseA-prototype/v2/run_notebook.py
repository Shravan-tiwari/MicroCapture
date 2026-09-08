"""Execute boundary_prototype_v2.ipynb in a real Jupyter kernel and save every
figure it produces to disk.

Why this exists: reviewing this pipeline from summary statistics alone is not
enough -- a paper mask can have a perfectly reasonable area fraction while
covering the wrong region, and only looking at the picture catches that.  This
runs the ACTUAL notebook (same cells the operator runs, real kernel, real
matplotlib output), writes the executed copy with outputs embedded, and dumps
each figure as a PNG so the rendered result can be inspected directly.

By default the outputs are written back INTO boundary_prototype_v2.ipynb, so the
notebook the operator has open in Jupyter/VS Code shows the plots after a run,
exactly as if they had pressed Run All themselves.  Without this the run happens
in a separate executed copy and the operator's own file stays blank -- which is
confusing and looks like nothing ran.  Pass --no-inplace to keep the original
untouched.

Usage:
    python3 run_notebook.py                    # run, write outputs into the notebook
    python3 run_notebook.py --no-inplace       # leave the source notebook blank
    python3 run_notebook.py --out run1         # figures to run1/
    python3 run_notebook.py --timeout 1800
"""
import argparse
import base64
import json
import os
import shutil
import sys

import nbformat
from nbclient import NotebookClient


HERE = os.path.dirname(os.path.abspath(__file__))
NB = os.path.join(HERE, 'boundary_prototype_v2.ipynb')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--nb', default=NB)
    ap.add_argument('--out', default='run_output')
    ap.add_argument('--timeout', type=int, default=1800)
    ap.add_argument('--no-inplace', action='store_true',
                    help="don't write outputs back into the source notebook")
    args = ap.parse_args()

    outdir = os.path.join(HERE, args.out)
    if os.path.isdir(outdir):
        shutil.rmtree(outdir)
    os.makedirs(outdir)

    nb = nbformat.read(args.nb, as_version=4)
    client = NotebookClient(
        nb,
        timeout=args.timeout,
        kernel_name='python3',
        resources={'metadata': {'path': HERE}},   # so `import bookcv` resolves
        allow_errors=True,
    )
    print(f'executing {os.path.basename(args.nb)} ...')
    client.execute()

    executed = os.path.join(outdir, 'executed.ipynb')
    nbformat.write(nb, executed)

    if not args.no_inplace:
        # Write the outputs back into the notebook the operator actually opens,
        # so the plots are visible there rather than only in the executed copy.
        nbformat.write(nb, args.nb)

    n_fig = 0
    n_err = 0
    log = []
    for i, cell in enumerate(nb.cells):
        if cell.cell_type != 'code':
            continue
        for out in cell.get('outputs', []):
            if out.output_type == 'error':
                n_err += 1
                msg = f"CELL {i} ERROR: {out.ename}: {out.evalue}"
                print(msg)
                log.append(msg)
                for line in out.get('traceback', [])[-6:]:
                    print('   ', line)
            elif out.output_type == 'stream':
                txt = ''.join(out.text)
                log.append(f'--- cell {i} stdout ---\n{txt}')
            elif out.output_type in ('display_data', 'execute_result'):
                data = out.get('data', {})
                if 'image/png' in data:
                    n_fig += 1
                    fn = os.path.join(outdir, f'cell{i:02d}_fig{n_fig:02d}.png')
                    with open(fn, 'wb') as fh:
                        fh.write(base64.b64decode(data['image/png']))

    with open(os.path.join(outdir, 'stdout.txt'), 'w') as fh:
        fh.write('\n'.join(log))

    # Also export a standalone HTML report.  VS Code keeps its own in-memory copy
    # of an open notebook and does not reliably reload a 40MB file changed
    # underneath it, so a run can look like it did nothing.  The HTML opens in a
    # browser with no editor state involved.
    html = os.path.join(HERE, 'report.html')
    try:
        from nbconvert import HTMLExporter
        body, _ = HTMLExporter().from_notebook_node(nb)
        with open(html, 'w') as fh:
            fh.write(body)
        print(f'html report -> {html}')
    except ImportError:
        print('nbconvert not installed -- skipping HTML report')

    print(f'\n{n_fig} figures -> {outdir}')
    print(f'{n_err} cell errors')
    print(f'executed notebook -> {executed}')
    return 1 if n_err else 0


if __name__ == '__main__':
    sys.exit(main())
