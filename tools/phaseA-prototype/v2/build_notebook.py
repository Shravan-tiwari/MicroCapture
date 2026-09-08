"""Generates boundary_prototype_v2.ipynb.

The notebook is generated rather than hand-edited so its cells stay in sync with
bookcv.py / viz.py and can be regenerated as stages are added in later phases.
Run:  python3 build_notebook.py
"""
import json
import os

CELLS = []


def _lines(src):
    """nbformat wants each source line to KEEP its trailing newline.  Splitting
    without them concatenates the whole cell into one line, which nbformat still
    validates but a real kernel then fails to parse (SyntaxError on execute)."""
    return src.strip("\n").splitlines(keepends=True)


def md(src):
    CELLS.append({"cell_type": "markdown", "metadata": {},
                  "source": _lines(src)})


def code(src):
    CELLS.append({"cell_type": "code", "metadata": {}, "outputs": [],
                  "execution_count": None,
                  "source": _lines(src)})


# ────────────────────────────────────────────────────────────────
md(r"""
# MicroCapture — Book Page Pipeline v2 (classical CV)

**Phase 0: batch harness + baseline.**

This notebook replaces the single-image `boundary_prototype.ipynb` workflow. That
notebook processed **one uploaded image**, which is how its thresholds ended up
tuned to one photo and failing on the next.

Everything here runs over **all 130 images** in `Dataset-Training/output/`, and every
stage shows:

1. a **contact sheet of all 130** with the stage's overlay drawn on each,
2. a **histogram** of the stage's key metric,
3. the **auto-flagged failures, shown large**, with the reason printed,
4. the metric **broken out by content class**.

## Why content class matters

Measured on this corpus, the `|Gx|` column profile the old pipeline traced is **not
separable from page content**:

| content class | n | peak/interior ratio | rival columns |
|---|---|---|---|
| DIAGRAM/SPARSE | 32 | **4.82** | 53 |
| MIXED | 44 | 3.63 | 55 |
| TEXT-HEAVY | 24 | **3.00** | **196** |
| IMAGE-HEAVY | 26 | **2.92** | 122 |
| EMPTY/BLANK | 4 | 7.97 | 89 |

On image 43 the text body reaches `|Gx|` 80–95 while the true page edge peaks at ~95 —
**644 columns rival the true edge**. No threshold separates them. This is the measured
root cause of the "behaves differently for text-heavy / diagram-heavy / image-heavy"
behaviour, and it drives the v2 design: **stages key on region properties (paper
brightness), which barely move when the printed content changes, not on gradient
strength.**
""")

code(r"""
import sys, os, glob, time, json
sys.path.insert(0, os.path.abspath('.'))

import cv2
import numpy as np
import matplotlib.pyplot as plt

import bookcv as B
import viz as V

# Reload on edit, so tweaking bookcv.py doesn't need a kernel restart.
%load_ext autoreload
%autoreload 2

DATA = os.path.expanduser(
    '~/Micrographics/MicroCapture/Dataset-Training/output')
FILES = sorted(glob.glob(os.path.join(DATA, '*.jpg')))
print(f'{len(FILES)} images found in {DATA}')
im0 = cv2.imread(FILES[0])
print('native resolution:', im0.shape[1], 'x', im0.shape[0])
print('working width    :', B.WORK_W)
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 1. Run all stages over all 130 images

Pure stage functions, one pass, results kept in memory. ~0.12 s/image → ~15 s total,
which is fast enough to re-run after every threshold change.
""")

code(r"""
def run_all(files=FILES, work_w=B.WORK_W):
    out = []
    t0 = time.time()
    for n, f in enumerate(files):
        raw = cv2.imread(f)
        work, scale = B.to_work(raw, work_w)

        t = time.time()
        dsk = B.deskew(work)                       # stage 1
        pap = B.segment_paper(dsk.rotated)         # stage 2
        cls = B.classify_content(dsk.rotated, pap.mask)
        sig = B.edge_signal(dsk.rotated)
        ms = (time.time() - t) * 1000

        out.append(dict(
            idx=n + 1, name=os.path.basename(f), path=f,
            work=work, scale=scale,
            deskew=dsk, paper=pap, content=cls, signal=sig, ms=ms))
        if (n + 1) % 25 == 0:
            print(f'  ...{n+1}/{len(files)}', flush=True)
    print(f'done: {len(out)} images in {time.time()-t0:.1f}s')
    return out

R = run_all()

# Flatten the fields we plot repeatedly.
IDX      = [r['idx'] for r in R]
NAMES    = [f"#{r['idx']}" for r in R]
LABELS   = [r['content'].label for r in R]
ANGLES   = [r['deskew'].angle for r in R]
PAPER    = [r['paper'].area_frac for r in R]
RECTFILL = [r['paper'].rect_fill for r in R]
CONTRAST = [r['paper'].contrast for r in R]
PTR      = [r['signal'].peak_to_interior for r in R]
RIVALS   = [r['signal'].rival_columns for r in R]
MS       = [r['ms'] for r in R]

from collections import Counter
print()
print('content classes:', Counter(LABELS).most_common())
print(f'per-image time: median {np.median(MS):.0f} ms')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 2. The corpus — every image, grouped by content class

First look at what we are actually working with. Tiles are grouped so each content
class can be judged as a group.
""")

code(r"""
for cls in B.CLASS_ORDER:
    sel = [r for r in R if r['content'].label == cls]
    if not sel:
        continue
    V.grid([r['work'] for r in sel],
           [f"#{r['idx']}" for r in sel],
           cols=8, width=200,
           title=f'{cls}  —  {len(sel)} images')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 3. Difficulty probe — why content type predicts failure

`peak/interior ratio` = strength of the strongest column gradient divided by the median
gradient inside the page. **High = the page edge stands out. Low = page content is as
strong as the edge**, and a gradient-tracing detector has nothing to lock onto.

This is the plot that justifies the whole v2 redesign.
""")

code(r"""
V.by_class(PTR, LABELS,
           title='Edge/content separability by content class  (higher = easier)',
           ylabel='peak / interior |Gx| ratio')

V.by_class(RIVALS, LABELS,
           title='Competing columns (>50% of peak strength)  — lower = easier',
           ylabel='rival column count')

V.hist(PTR, title='peak/interior ratio — whole corpus',
       xlabel='ratio',
       vlines=[(2.0, 'ratio 2.0 — tracer cannot separate', 'red'),
               (3.0, 'ratio 3.0 — marginal', 'orange')])

hard = [i for i, v in enumerate(PTR) if v < 2.0]
print(f'{len(hard)} images below ratio 2.0 (gradient tracing unreliable):',
      [R[i]['idx'] for i in hard])
""")

code(r"""
# The mechanism, on three images: an easy one, a hard one, and a dark cover.
fig, axes = plt.subplots(3, 2, figsize=(16, 11))
for row, idx in enumerate([43, 96, 17]):
    r = R[idx - 1]
    axes[row, 0].imshow(cv2.cvtColor(V.thumb(r['work'], 420), cv2.COLOR_BGR2RGB))
    axes[row, 0].axis('off')
    axes[row, 0].set_title(f"#{idx}  {r['content'].label}  "
                           f"ratio={r['signal'].peak_to_interior:.2f}")
    p = r['signal'].profile
    axes[row, 1].plot(p, lw=0.8)
    axes[row, 1].axhline(0.5 * p.max(), color='r', ls='--', lw=1,
                         label='50% of max')
    axes[row, 1].set_title('|Gx| column profile — what the old tracer walks on')
    axes[row, 1].legend(fontsize=8)
    axes[row, 1].grid(alpha=0.3)
plt.tight_layout()
plt.show()
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 4. Stage 1 — Deskew

Ported from the operator's `bookcurve (8).ipynb`: Otsu → largest contour → `minAreaRect`
→ rotate. It keys on the **book silhouette**, not page content, so it is content-invariant
by construction — a blank page deskews as well as a text page.

Reference run: 130/130, range −16.66°…+15.33°, mean abs 2.68°.

**Known limitation:** `normalize_angle` maps to ±45°, so this corrects *skew*, not
*orientation* — it cannot tell 0° from 90°.
""")

code(r"""
V.hist(ANGLES, title='Stage 1 — detected skew angle, all 130',
       xlabel='degrees', bins=40)
V.by_class(np.abs(ANGLES), LABELS,
           title='|skew angle| by content class — should be FLAT if content-invariant',
           ylabel='|degrees|')

big = [abs(a) > 10 for a in ANGLES]
V.scorecard(LABELS, [not b for b in big], 'Stage 1 deskew (|angle| <= 10 deg)')
""")

code(r"""
# Every image, deskewed, with its angle. Flagged = large rotation, worth eyeballing.
V.grid([r['deskew'].rotated for r in R],
       [f"#{r['idx']}  {r['deskew'].angle:+.1f}°" for r in R],
       cols=8, width=200, flags=big,
       title='Stage 1 — deskewed output, ALL 130 (red = |angle| > 10°)')
""")

code(r"""
# Before/after for the largest rotations — the cases most likely to be wrong.
order = np.argsort(-np.abs(ANGLES))[:6]
fig, axes = plt.subplots(len(order), 2, figsize=(13, 3.4 * len(order)))
for row, i in enumerate(order):
    r = R[i]
    axes[row, 0].imshow(cv2.cvtColor(V.thumb(r['work'], 380), cv2.COLOR_BGR2RGB))
    axes[row, 0].set_title(f"#{r['idx']} original"); axes[row, 0].axis('off')
    axes[row, 1].imshow(cv2.cvtColor(V.thumb(r['deskew'].rotated, 380),
                                     cv2.COLOR_BGR2RGB))
    axes[row, 1].set_title(f"deskewed {r['deskew'].angle:+.2f}°")
    axes[row, 1].axis('off')
plt.tight_layout(); plt.show()
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 5. Stage 2 — Paper-region segmentation

**The core design change.** Instead of "where is the strongest vertical gradient"
(which text defeats), we ask **"which pixels are paper"**. Paper stays bright where it
is printed on — dense text moves a neighbourhood's mean by tens of levels, while the
paper-to-stand step is ~185 levels here. So a region test is nearly content-invariant.

Thresholding is **Otsu on the whole frame**. A fixed offset from the corner background
was tried first and failed on ~26 of the 130: images 90–121 have a lit wall behind the
stand, so corner patches read ~47 while the frame median is ~150, and the wall floods
in as paper (measured paper fractions 0.77–0.98 where the book covers about half the
frame). Otsu splits on the image's own bimodal histogram and puts the wall on the dark
side where it belongs.
""")

code(r"""
V.hist(PAPER, title='Stage 2 — paper area as fraction of frame, all 130',
       xlabel='paper fraction',
       vlines=[(0.10, 'too small — under-segmented', 'red'),
               (0.75, 'too large — background leaked in', 'red')])

V.by_class(PAPER, LABELS,
           title='Paper fraction by content class — should be FLAT if content-invariant',
           ylabel='paper area fraction')

V.by_class(CONTRAST, LABELS,
           title='Paper-to-background contrast by content class',
           ylabel='levels')

# Shape, not just area. An open book nearly fills its own minAreaRect; a mask with a
# wall blob or hand fused on does not. This metric exists because area alone passed
# ~40 visibly contaminated masks -- the blob kept the area inside the accepted window.
V.hist(RECTFILL, title='Stage 2 - mask fill of its own minAreaRect (shape check)',
       xlabel='filled fraction',
       vlines=[(0.75, '0.75 - blob attached below this', 'red')])
V.by_class(RECTFILL, LABELS,
           title='Mask rectangularity by content class',
           ylabel='rect fill fraction')

paper_bad = [(p < 0.10 or p > 0.75 or rf < 0.75)
              for p, rf in zip(PAPER, RECTFILL)]
V.scorecard(LABELS, [not b for b in paper_bad],
            'Stage 2 paper segmentation (0.10 <= fraction <= 0.75)')
""")

code(r"""
# ALL 130 with the paper mask tinted and its bounding box drawn.
ov = [V.draw_bbox(V.overlay_mask(r['deskew'].rotated, r['paper'].mask,
                                 (0, 0, 255), 0.40),
                  r['paper'].bbox, (0, 255, 0), 3) for r in R]
V.grid(ov, [f"#{r['idx']} {r['paper'].area_frac:.2f}" for r in R],
       cols=8, width=200, flags=paper_bad,
       title='Stage 2 — paper mask (red) + bbox (green), ALL 130')
""")

code(r"""
reasons = [r['paper'].note or
           ('paper fraction %.2f out of range' % r['paper'].area_frac)
           for r in R]
V.show_flagged(ov, NAMES, paper_bad, reasons, cols=3, width=520,
               title='Stage 2 FLAGGED — inspect these')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 6. Phase 0 baseline scorecard

Where we stand **before** any boundary/gutter/finger work. Every later change is measured
against this table.
""")

code(r"""
print('=' * 64)
print('PHASE 0 BASELINE'.center(64))
print('=' * 64)
V.scorecard(LABELS, [not b for b in big],      'Stage 1  deskew')
V.scorecard(LABELS, [not b for b in paper_bad],'Stage 2  paper segmentation')

print(f"\nper-image time: median {np.median(MS):.0f} ms  "
      f"(full corpus {np.sum(MS)/1000:.1f}s)")
print(f"skew angle    : {np.min(ANGLES):+.2f}° .. {np.max(ANGLES):+.2f}°  "
      f"mean|.| {np.mean(np.abs(ANGLES)):.2f}°")
print(f"paper fraction: {np.min(PAPER):.3f} .. {np.max(PAPER):.3f}  "
      f"median {np.median(PAPER):.3f}")

rows = [dict(idx=r['idx'], name=r['name'], label=r['content'].label,
             angle=round(r['deskew'].angle, 2),
             paper=round(r['paper'].area_frac, 4),
             contrast=round(r['paper'].contrast, 1),
             rect_fill=round(r['paper'].rect_fill, 4),
             ptr=round(r['signal'].peak_to_interior, 3),
             rivals=r['signal'].rival_columns,
             note=r['paper'].note, ms=round(r['ms'], 1)) for r in R]
with open('phase0_baseline.json', 'w') as fh:
    json.dump(rows, fh, indent=1)
print('\nwrote phase0_baseline.json')
""")

md(r"""
### Known open issues at end of Phase 0

Recorded here so they are not silently carried forward:

1. **Residual wall blob on ~6 images** (#109, #112, #117, #121–123). The occupancy trim
   removed it from the majority (#97–107, #113, #119, #124–130 are now clean), but where
   the blob overlaps the book's own row band it survives both the occupancy trim and
   `rect_fill` (min is now 0.832, so nothing flags). Needs a left-edge verticality test in
   Stage 3 — the book's true side edge is a long straight near-vertical line, the blob's is
   not.
2. **Images 17, 18 (black covers)** — paper fraction 0.05/0.07; only the bright title
   strip is caught. Brightness cannot segment a black cover against a dark stand. These
   are single *closed* books, not open spreads — scope to be confirmed.
3. **The fore-edge block is included in the paper mask.** Correct at this stage: the mask
   is *paper*, and the block *is* paper. Separating the front page from the block is
   Stage 3's job (the "front pages, not side pages" requirement).

### A metric lesson from Phase 0

The first version of this stage scored **98% pass on area fraction alone**, and the
contact sheet showed that was wrong: a lit wall behind the stand was fused into roughly
**40** of the masks (images ~90–130). The blob kept the area inside the accepted window,
so the metric never fired, and because the wall is *connected* to the book, taking the
largest component could not drop it either.

Two fixes came out of that, both kept:

* the paper threshold is now anchored on the **paper mode** (`bg + 0.62*contrast`, floored
  at Otsu) rather than on Otsu alone — paper sits at 220–240 while the wall sits at
  90–118, a 116+ level gap on every image measured, whereas Otsu was landing at 126–148
  and cutting *below* the wall;
* a **shape** metric (`rect_fill`, how much of its own `minAreaRect` the mask fills) now
  runs beside the area metric — an open book is nearly a filled rectangle;
* and a **column/row occupancy trim**: a wall or hand blob occupies ~0.17–0.19 of a
  column's height while a real book column occupies ~0.7+, so keeping only the run of
  columns containing the occupancy peak separates them structurally rather than by
  threshold. This is what finally removed most of the blobs; raising the brightness
  threshold alone shrank them but could not remove them, because the wall is *connected*
  to the book and convex enough that `rect_fill` caught only one.

The general point, which applies to every stage after this one: **an aggregate metric can
pass while the picture is plainly wrong.** Every stage gets a contact sheet of all 130 for
this reason, and a stage is not "done" until the sheet has actually been looked at.

### Next — Phase 1: front-page boundary

Search **inward** from the paper mask's border to find the printed page edge, rather
than globally for the strongest gradient. The mask constrains the search band, which is
what removes the 644 competing columns on image 43.
""")

# ────────────────────────────────────────────────────────────────
nb = {
    "cells": CELLS,
    "metadata": {
        "kernelspec": {"display_name": "Python 3", "language": "python",
                       "name": "python3"},
        "language_info": {"name": "python", "version": "3.12"},
    },
    "nbformat": 4,
    "nbformat_minor": 5,
}

out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   'boundary_prototype_v2.ipynb')
with open(out, 'w') as fh:
    json.dump(nb, fh, indent=1)
print('wrote', out, f'({len(CELLS)} cells)')
