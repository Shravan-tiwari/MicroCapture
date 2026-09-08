"""Generates boundary_prototype_v2.ipynb.

The notebook is generated rather than hand-edited so its cells stay in sync with
bookcv.py / viz.py and can be regenerated as stages are added in later phases.
Run:  python3 build_notebook.py
"""
import json
import os

CELLS = []

# Stage source is loaded from _sections.json rather than embedded in a
# code(r"""...""") literal: the stage functions contain their own """ docstrings,
# which would terminate the wrapper early.
import json as _json, os as _os
_SEC = _json.load(open(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)),
                                     '_sections.json')))


def code_section(key):
    """Emit one section of stage source as its own code cell."""
    CELLS.append({"cell_type": "code", "metadata": {}, "outputs": [],
                  "execution_count": None,
                  "source": _lines(_SEC[key])})



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
import os, glob, time, json, math
from dataclasses import dataclass, field
from typing import Optional, List, Tuple

import cv2
import numpy as np
import matplotlib.pyplot as plt

DATA = os.path.expanduser(
    '~/Micrographics/MicroCapture/Dataset-Training/output')
FILES = sorted(glob.glob(os.path.join(DATA, '*.jpg')))
print(f'{len(FILES)} images found in {DATA}')
im0 = cv2.imread(FILES[0])
print('native resolution:', im0.shape[1], 'x', im0.shape[0])
""")

md(r"""
### Configuration and shared helpers
""")
code_section("config")

md(r"""
### Display helpers

Every stage below shows **all 130** images: a contact sheet with the
stage's overlay, a metric histogram, the auto-flagged failures large with reasons, and the
metric by content class. No stage is ever judged on a single picture.
""")
code_section("viz")

md(r"""
## 1. Stage code — the whole pipeline

Every algorithm lives here, in the notebook. Edit a function and re-run this cell, then
re-run the driver below to see the effect across all 130 images. Nothing is imported from
an external module.
""")

md(r"""
### Stage 1 — deskew
Ported from `bookcurve (8).ipynb`: Otsu -> largest contour -> minAreaRect -> rotate. Keys on
the book SILHOUETTE, not page content, so it is content-invariant by construction.
""")
code_section("deskew")

md(r"""
### Stage 2 — paper-region segmentation
The core change vs the old pipeline: segment paper as a REGION instead of tracing the
strongest gradient, which page content defeats.
""")
code_section("paper")

md(r"""
### Scope gate + content classifier
Rejects closed books; keeps single sheets. The classifier is diagnostic only and never
routes the algorithm.
""")
code_section("scope_content")

md(r"""
### Stage 3 — front-page boundary and gutter
Bounds the printed pages, excluding the fore-edge block. Keyed on per-column mean
brightness, which content barely perturbs.
""")
code_section("boundary")

md(r"""
### Stage 3b — top/bottom edge trace and notch gutter
Ported from the original `boundary_prototype.ipynb`. Reads the gutter off the SHAPE of the
traced top/bottom edges rather than looking for it directly.
""")
code_section("tracer")

md(r"""
### Stage 4 — finger mask and page-colour fill
""")
code_section("finger")

md(r"""
### Difficulty probe
Measures how badly page content competes with the page edge in the |Gx| profile.
""")
code_section("probe")


# ────────────────────────────────────────────────────────────────
md(r"""
## 2. Run every stage over all 130 images

Pure stage functions, one pass, results kept in memory. ~0.12 s/image → ~15 s total,
which is fast enough to re-run after every threshold change.
""")

code(r"""
def run_all(files=FILES, work_w=WORK_W):
    out = []
    t0 = time.time()
    for n, f in enumerate(files):
        raw = cv2.imread(f)
        work, scale = to_work(raw, work_w)

        t = time.time()
        dsk = deskew(work)                       # stage 1
        pap = segment_paper(dsk.rotated)         # stage 2
        scp = check_scope(dsk.rotated, pap)      # scope gate
        bnd = detect_boundary(dsk.rotated, pap, scp) if scp.in_scope else None
        trc = trace_edges(dsk.rotated, pap, bnd) if bnd else None
        fng = detect_fingers(dsk.rotated, pap, bnd) if bnd else None
        cls = classify_content(dsk.rotated, pap.mask)
        sig = edge_signal(dsk.rotated)
        ms = (time.time() - t) * 1000

        out.append(dict(
            idx=n + 1, name=os.path.basename(f), path=f,
            work=work, scale=scale,
            deskew=dsk, paper=pap, scope=scp, bound=bnd, trace=trc, finger=fng,
            content=cls, signal=sig, ms=ms))
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
KIND     = [r['scope'].kind for r in R]
INSCOPE  = [r['scope'].in_scope for r in R]
WHITE    = [r['scope'].white_frac for r in R]
ASPECT   = [r['scope'].aspect for r in R]
SPAN     = [((r['bound'].right - r['bound'].left) / float(r['paper'].bbox[2]))
            if r['bound'] else 0.0 for r in R]
HASGUT   = [bool(r['bound'] and r['bound'].gutter is not None) for r in R]
BLOCKL   = [r['bound'].block_left if r['bound'] else 0 for r in R]
NOTCH    = [bool(r['trace'] and r['trace'].notch_found) for r in R]
GUTTER2  = [(r['trace'].gutter_x if r['trace'] else None) for r in R]
NFING    = [r['finger'].n_regions if r['finger'] else 0 for r in R]
FAREA    = [r['finger'].area_frac if r['finger'] else 0.0 for r in R]
OVERTEXT = [bool(r['finger'] and r['finger'].over_text) for r in R]
CONTRAST = [r['paper'].contrast for r in R]
PTR      = [r['signal'].peak_to_interior for r in R]
RIVALS   = [r['signal'].rival_columns for r in R]
MS       = [r['ms'] for r in R]

from collections import Counter
print()
print('content classes:', Counter(LABELS).most_common())
print('capture kinds  :', Counter(KIND).most_common())
print(f'per-image time: median {np.median(MS):.0f} ms')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 3. The corpus — every image, grouped by content class

First look at what we are actually working with. Tiles are grouped so each content
class can be judged as a group.
""")

code(r"""
for cls in CLASS_ORDER:
    sel = [r for r in R if r['content'].label == cls]
    if not sel:
        continue
    grid([r['work'] for r in sel],
           [f"#{r['idx']}" for r in sel],
           cols=8, width=200,
           title=f'{cls}  —  {len(sel)} images')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 4. Difficulty probe — why content type predicts failure

`peak/interior ratio` = strength of the strongest column gradient divided by the median
gradient inside the page. **High = the page edge stands out. Low = page content is as
strong as the edge**, and a gradient-tracing detector has nothing to lock onto.

This is the plot that justifies the whole v2 redesign.
""")

code(r"""
by_class(PTR, LABELS,
           title='Edge/content separability by content class  (higher = easier)',
           ylabel='peak / interior |Gx| ratio')

by_class(RIVALS, LABELS,
           title='Competing columns (>50% of peak strength)  — lower = easier',
           ylabel='rival column count')

hist(PTR, title='peak/interior ratio — whole corpus',
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
    axes[row, 0].imshow(cv2.cvtColor(thumb(r['work'], 420), cv2.COLOR_BGR2RGB))
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
## 5. Stage 1 results — Deskew

Ported from the operator's `bookcurve (8).ipynb`: Otsu → largest contour → `minAreaRect`
→ rotate. It keys on the **book silhouette**, not page content, so it is content-invariant
by construction — a blank page deskews as well as a text page.

Reference run: 130/130, range −16.66°…+15.33°, mean abs 2.68°.

**Known limitation:** `normalize_angle` maps to ±45°, so this corrects *skew*, not
*orientation* — it cannot tell 0° from 90°.
""")

code(r"""
hist(ANGLES, title='Stage 1 — detected skew angle, all 130',
       xlabel='degrees', bins=40)
by_class(np.abs(ANGLES), LABELS,
           title='|skew angle| by content class — should be FLAT if content-invariant',
           ylabel='|degrees|')

big = [abs(a) > 10 for a in ANGLES]
scorecard(LABELS, [not b for b in big], 'Stage 1 deskew (|angle| <= 10 deg)')
""")

code(r"""
# Every image, deskewed, with its angle. Flagged = large rotation, worth eyeballing.
grid([r['deskew'].rotated for r in R],
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
    axes[row, 0].imshow(cv2.cvtColor(thumb(r['work'], 380), cv2.COLOR_BGR2RGB))
    axes[row, 0].set_title(f"#{r['idx']} original"); axes[row, 0].axis('off')
    axes[row, 1].imshow(cv2.cvtColor(thumb(r['deskew'].rotated, 380),
                                     cv2.COLOR_BGR2RGB))
    axes[row, 1].set_title(f"deskewed {r['deskew'].angle:+.2f}°")
    axes[row, 1].axis('off')
plt.tight_layout(); plt.show()
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 6. Stage 2 results — Paper segmentation

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
hist(PAPER, title='Stage 2 — paper area as fraction of frame, all 130',
       xlabel='paper fraction',
       vlines=[(0.10, 'too small — under-segmented', 'red'),
               (0.75, 'too large — background leaked in', 'red')])

by_class(PAPER, LABELS,
           title='Paper fraction by content class — should be FLAT if content-invariant',
           ylabel='paper area fraction')

by_class(CONTRAST, LABELS,
           title='Paper-to-background contrast by content class',
           ylabel='levels')

# Shape, not just area. An open book nearly fills its own minAreaRect; a mask with a
# wall blob or hand fused on does not. This metric exists because area alone passed
# ~40 visibly contaminated masks -- the blob kept the area inside the accepted window.
hist(RECTFILL, title='Stage 2 - mask fill of its own minAreaRect (shape check)',
       xlabel='filled fraction',
       vlines=[(0.75, '0.75 - blob attached below this', 'red')])
by_class(RECTFILL, LABELS,
           title='Mask rectangularity by content class',
           ylabel='rect fill fraction')

# Out-of-scope captures (closed books) are excluded: their masks are SUPPOSED to be
# poor, so scoring them as segmentation failures would understate the stage.
paper_bad = [ins and (p < 0.10 or p > 0.75 or rf < 0.75)
             for p, rf, ins in zip(PAPER, RECTFILL, INSCOPE)]
scorecard(LABELS, [not b for b in paper_bad],
            'Stage 2 paper segmentation (0.10 <= fraction <= 0.75)')
""")

code(r"""
# ALL 130 with the paper mask tinted and its bounding box drawn.
ov = [draw_bbox(overlay_mask(r['deskew'].rotated, r['paper'].mask,
                                 (0, 0, 255), 0.40),
                  r['paper'].bbox, (0, 255, 0), 3) for r in R]
grid(ov, [f"#{r['idx']} {r['paper'].area_frac:.2f}" for r in R],
       cols=8, width=200, flags=paper_bad,
       title='Stage 2 — paper mask (red) + bbox (green), ALL 130')
""")

code(r"""
reasons = [r['paper'].note or
           ('paper fraction %.2f out of range' % r['paper'].area_frac)
           for r in R]
show_flagged(ov, NAMES, paper_bad, reasons, cols=3, width=520,
               title='Stage 2 FLAGGED — inspect these')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 7. Scope gate — reject closed books, keep single sheets

Closed books (a photographed cover) are **out of scope**: no page to flatten, no gutter,
no split. Single-page documents (flyers, magazine covers, brochures) **stay in scope** —
they are genuinely *easier* than spreads, measuring `rect_fill` 0.88–0.997 against
0.95–0.99 for spreads. They simply take the no-gutter path.

The discriminator is the **fraction of the detected region that is near-white paper**,
because it keys on what the material physically *is*. Two simpler signals were tried and
both failed:

* **area fraction** — #43, a valid spread, sits at 0.226, right among the closed books;
* **region brightness** — #71/#73, valid single sheets, have median 102–104, *below*
  several closed books at 112–153.

White fraction separates cleanly: closed books 0.010–0.069, valid pages 0.157–0.924.
Aspect resolves the two edge cases — a full-bleed colour spread with no white margin
(#115, white 0.093) is accepted because it is landscape, and a dark cover where only the
title strip segments (#17/#18, aspect 3.77/2.48) is rejected because a real spread never
exceeds ~1.6.
""")

code(r"""
plt.figure(figsize=(9, 5.5))
cols = {'spread': '#2e7d32', 'single': '#1565c0', 'closed-book': '#c62828'}
for k, c in cols.items():
    ix = [i for i, kk in enumerate(KIND) if kk == k]
    if not ix:
        continue
    plt.scatter([WHITE[i] for i in ix], [ASPECT[i] for i in ix],
                c=c, label=f'{k} (n={len(ix)})', s=42, alpha=0.8,
                edgecolors='white', linewidths=0.5)
    for i in ix:
        if KIND[i] == 'closed-book':
            plt.annotate(f'#{IDX[i]}', (WHITE[i], ASPECT[i]),
                         fontsize=8, xytext=(4, 3), textcoords='offset points')
plt.axvline(0.12, color='red', ls='--', lw=1.2, label='white 0.12')
plt.axhline(1.10, color='gray', ls=':', lw=1.2, label='aspect 1.10 / 2.00')
plt.axhline(2.00, color='gray', ls=':', lw=1.2)
plt.xlabel('fraction of region that is near-white paper')
plt.ylabel('region aspect (w/h)')
plt.title('Scope gate — closed books separate on white fraction + aspect')
plt.legend(fontsize=8); plt.grid(alpha=0.3); plt.tight_layout(); plt.show()

scorecard(LABELS, INSCOPE, 'Scope gate (in-scope captures)')
""")

code(r"""
# Every rejected capture, shown large. These must ALL be closed books --
# a valid page appearing here is a false positive and a bug.
rej = [not s for s in INSCOPE]
show_flagged([r['work'] for r in R], NAMES, rej,
               [r['scope'].reason for r in R], cols=3, width=460,
               title='REJECTED as out of scope — verify every one is a closed book')

# And the single-sheet captures, which stay in scope on the no-gutter path.
sng = [k == 'single' for k in KIND]
grid([R[i]['work'] for i, b in enumerate(sng) if b],
       [f"#{R[i]['idx']}" for i, b in enumerate(sng) if b],
       cols=8, width=210,
       title='IN SCOPE — single-page documents (no gutter, no split)')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 8. Stage 3 results — front-page boundary and gutter

The operator's requirement: bound the **printed left and right pages**, not the side
pages. Between the page and the stand sits the **fore-edge block** — the stack of
remaining leaves, which *is* paper and therefore inside the paper mask — and it must be
excluded.

Evidence is the per-column **mean brightness** of the paper region, not a gradient.
Content barely perturbs a column mean (it is a few levels of high-frequency noise on a
~220 plateau), which is exactly why this works where gradient tracing failed.

Two failures during development, both found by looking at the plots rather than the
numbers, and both worth recording:

1. **A plain longest-run returned one page, not the spread.** The gutter is a deep but
   *narrow* spike that cuts below the on-page threshold and splits the run in two — on
   #124 the result was `left=957 right=1491`, the right-hand page alone. Fixed by
   morphologically closing gaps up to 8% of the book width before taking the run: a
   gutter is tens of columns wide, the block is hundreds, so this bridges the spine
   without ever bridging the block. Gutter detection went from 14/108 to 96/108.
2. **Ink-heavy pages under-detected badly.** On full-bleed photo spreads the ink drags
   the column mean below "page level" and the run stops short — 37 of 124 in-scope
   captures, some down to span 0.20 of the paper bbox, every one with white fraction
   ≤ 0.75. Fixed by using Stage 2's paper mask extent as the outer bound (ink barely
   moves paper-vs-stand segmentation) and trimming inward only where a genuinely
   *darker plateau* is present, capped at 25% of width. Under-detection fell from 37 to 4.
""")

code(r"""
inb = [i for i, r in enumerate(R) if r['bound']]
spreads = [i for i in inb if KIND[i] == 'spread']
singles = [i for i in inb if KIND[i] == 'single']
print(f'gutter found on {sum(HASGUT[i] for i in spreads)}/{len(spreads)} spreads')
print(f'gutter wrongly found on {sum(HASGUT[i] for i in singles)}/{len(singles)} singles'
      '  (must be 0 -- a single sheet has no gutter)')

hist([SPAN[i] for i in inb],
       title='Stage 3 - detected page span as fraction of the paper bbox',
       xlabel='span / bbox width',
       vlines=[(0.80, '0.80 - under-detection below this', 'red')])
by_class([SPAN[i] for i in inb], [LABELS[i] for i in inb],
           title='Page span by content class - FLAT means content-invariant',
           ylabel='span / bbox width')

span_bad = [bool(r['bound']) and s < 0.80 for r, s in zip(R, SPAN)]
scorecard([LABELS[i] for i in inb], [not span_bad[i] for i in inb],
            'Stage 3 boundary (span >= 0.80 of paper bbox)')
""")

code(r"""
def draw_bounds(r):
    o = r['deskew'].rotated.copy()
    b = r['bound']
    if b is None:
        return o
    cv2.line(o, (b.left, b.top), (b.left, b.bottom), (0, 255, 0), 4)
    cv2.line(o, (b.right, b.top), (b.right, b.bottom), (0, 255, 0), 4)
    if b.gutter is not None:
        cv2.line(o, (b.gutter, b.top), (b.gutter, b.bottom), (255, 0, 255), 4)
    return o

ovb = [draw_bounds(r) for r in R]
grid([ovb[i] for i in inb],
       [f"#{R[i]['idx']} {'g' if HASGUT[i] else '-'} {SPAN[i]:.2f}" for i in inb],
       cols=8, width=200, flags=[span_bad[i] for i in inb],
       title='Stage 3 - green = page edges, magenta = gutter, ALL in-scope captures')
""")

code(r"""
# The under-detected cases, large. Green lines cutting into the printed page here
# means the boundary is wrong and needs work -- not a metric to be tuned away.
show_flagged(ovb, NAMES, span_bad,
               [f'span {SPAN[i]:.2f} of bbox' for i in range(len(R))],
               cols=3, width=520, title='Stage 3 FLAGGED - under-detected span')

# Spreads where no gutter was found. Some are genuinely flat-lying books with no
# spine shadow; any that clearly show a spine are a miss.
nogut = [KIND[i] == 'spread' and not HASGUT[i] for i in range(len(R))]
show_flagged(ovb, NAMES, nogut,
               [R[i]['bound'].note if R[i]['bound'] else '' for i in range(len(R))],
               cols=3, width=520, title='Stage 3 - spreads with NO gutter detected')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 8b. Stage 3b results — traced edges and notch gutter

The original prototype's approach, and it is a better idea than the brightness dip used
in Stage 3. The gutter is a **crease, not an edge**: searching for it directly makes a
per-row scan wander by ~200px chasing text and shadow contrast near the spine. Instead,
trace the top and bottom page edges — which *are* strong and continuous — and read the
gutter off their **shape**. As the pages bend into the spine both curves dip toward each
other, producing a V-notch at the gutter by construction.

Why that matters here: a notch is a **geometric** consequence of the binding, so it
survives full-bleed colour and dense text, which flatten or fake a brightness dip. It also
yields the gutter as two anchor points with a real slope, rather than one vertical line.

Two guards carried over from the original, both learned the hard way there:

* the trace is **continuity-constrained** — a free per-column argmax over Gy falls into
  text-line edges, which are the same order of magnitude in the gradient as the true page
  edge, so each step may only move ±6 rows from the previous column;
* the notch must be a **genuine interior turning point with prominence**, never the most
  extreme value in the window — both curves are noisy and sloped, so a plain argmax often
  lands on the window boundary, which is not a turning point but simply where the search
  stopped.
""")

code(r"""
tr = [i for i, r in enumerate(R) if r['trace'] is not None]
spread_i = [i for i in tr if KIND[i] == 'spread']
print(f'notch gutter found on {sum(NOTCH[i] for i in spread_i)}/{len(spread_i)} spreads')
print(f'brightness-dip gutter (Stage 3) on {sum(HASGUT[i] for i in spread_i)}/{len(spread_i)}')

both = [i for i in spread_i if NOTCH[i] and HASGUT[i]]
d = [abs(GUTTER2[i] - R[i]['bound'].gutter) for i in both]
print(f'\nboth methods agree on {len(both)} spreads; |difference| in px:')
print(f'  median {np.median(d):.0f}   p90 {np.percentile(d, 90):.0f}   max {max(d)}')
hist(d, title='Gutter: notch method vs brightness dip - disagreement (px)',
     xlabel='|difference| px',
     vlines=[(40, '40px - materially different', 'red')])
""")

code(r"""
def draw_trace(r):
    o = r['deskew'].rotated.copy()
    t = r['trace']
    if t is None or t.top.size == 0:
        return o
    for i in range(t.top.size):
        x = t.x0 + i
        cv2.circle(o, (x, int(t.top[i])), 1, (0, 255, 255), -1)
        cv2.circle(o, (x, int(t.bottom[i])), 1, (255, 255, 0), -1)
    if t.notch_found:
        cv2.line(o, t.top_notch, t.bot_notch, (255, 0, 255), 5)
        cv2.circle(o, t.top_notch, 11, (0, 0, 255), -1)
        cv2.circle(o, t.bot_notch, 11, (0, 0, 255), -1)
    b = r['bound']
    if b.gutter is not None:      # Stage 3's answer, for comparison
        cv2.line(o, (b.gutter, b.top), (b.gutter, b.bottom), (0, 255, 0), 2)
    return o

ovt = [draw_trace(r) for r in R]
grid([ovt[i] for i in tr],
     [f"#{R[i]['idx']}{' N' if NOTCH[i] else ''}" for i in tr],
     cols=8, width=200, flags=[not NOTCH[i] and KIND[i] == 'spread' for i in tr],
     title='Stage 3b - traced top (yellow) / bottom (cyan) edges, magenta = notch gutter, '
           'green = Stage 3 brightness gutter')
""")

code(r"""
# Spreads where the notch method found no gutter.
miss = [KIND[i] == 'spread' and not NOTCH[i] for i in range(len(R))]
show_flagged(ovt, NAMES, miss,
             [(R[i]['trace'].note if R[i]['trace'] else '') for i in range(len(R))],
             cols=3, width=520, title='Stage 3b - no notch found')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 9. Stage 4 results — finger mask and page-colour fill

The requirement: **fill the finger region with the colour of the page.** Deliberately not
content reconstruction — nothing is invented that could pass as real text. A flat fill
either looks right (on a margin, the dominant case here: thumbs holding the book open) or
looks obviously like a patch, which is a safe failure.

Two things make this work that the old pipeline's skin detector did not have:

1. **Stage 3 already knows where the page is**, so only skin *inside* the page bounds is
   considered — a hand resting on the stand is irrelevant.
2. **Printed photos of people are rejected structurally.** A real finger enters from
   outside the page, so its region must touch the page boundary. A skin-toned region fully
   enclosed by page content never does, however finger-shaped it looks.

The chroma window had to be tightened after measuring: textbook YCrCb bounds
(Cr 135–180, Cb 85–135) fired on **105 of 124** captures with regions up to **53% of the
page** — aged cream book paper sits close to skin in chroma. Adding a luma test (skin is
markedly darker than lit paper, ratio < 0.82) and an area cap brought this to 69/124
(56%), median region 1.3% of page area, which matches the visible rate in the corpus.

The fill colour is sampled **locally** from surrounding page pixels, not as one global
page colour: a curved page shades noticeably from gutter to fore-edge, so a single flat
value would show as a mismatched rectangle.
""")

code(r"""
fin = [i for i, r in enumerate(R) if r['finger']]
print(f'fingers detected on {sum(1 for i in fin if NFING[i])} / {len(fin)} in-scope captures')
print(f'regions overlapping inked content: {sum(OVERTEXT)}')

hist([FAREA[i] for i in fin if NFING[i]],
       title='Stage 4 - finger region as fraction of page area',
       xlabel='area fraction',
       vlines=[(0.22, '0.22 - rejected above this', 'red')])
by_class([FAREA[i] for i in fin], [LABELS[i] for i in fin],
           title='Finger area by content class - IMAGE-HEAVY is the false-positive risk',
           ylabel='area fraction')
""")

code(r"""
# Mask overlay for every capture where a finger was found.
fmask = [i for i in fin if NFING[i]]
grid([overlay_mask(R[i]['deskew'].rotated, R[i]['finger'].mask, (0, 0, 255), 0.55)
        for i in fmask],
       [f"#{R[i]['idx']} {FAREA[i]:.3f}{' TEXT' if OVERTEXT[i] else ''}" for i in fmask],
       cols=8, width=200, flags=[OVERTEXT[i] for i in fmask],
       title='Stage 4 - detected finger mask (red). Red border = overlaps inked content')
""")

code(r"""
# Before / after fill, on the largest detections -- this is where a bad mask shows.
top = sorted(fmask, key=lambda i: -FAREA[i])[:8]
fig, axes = plt.subplots(len(top), 2, figsize=(13, 3.1 * len(top)))
for row, i in enumerate(top):
    r = R[i]
    axes[row, 0].imshow(cv2.cvtColor(thumb(r['deskew'].rotated, 420), cv2.COLOR_BGR2RGB))
    axes[row, 0].set_title(f"#{r['idx']} original"); axes[row, 0].axis('off')
    axes[row, 1].imshow(cv2.cvtColor(thumb(r['finger'].filled, 420), cv2.COLOR_BGR2RGB))
    axes[row, 1].set_title(f"filled with page colour ({FAREA[i]:.1%} of page)"
                           + ('  -- OVER TEXT' if OVERTEXT[i] else ''))
    axes[row, 1].axis('off')
plt.tight_layout(); plt.show()
""")

code(r"""
# Fingers sitting on inked content: the fill ERASES that text. Shown so the
# operator can decide policy (fill anyway vs flag for recapture) with the real
# cases in front of them rather than in the abstract.
show_flagged([r['finger'].filled if r['finger'] else r['work'] for r in R],
               NAMES, OVERTEXT,
               [r['finger'].note if r['finger'] else '' for r in R],
               cols=3, width=520,
               title='Stage 4 - finger over inked content (fill erases it)')
""")

# ────────────────────────────────────────────────────────────────
md(r"""
## 10. Scorecard

Where we stand **before** any boundary/gutter/finger work. Every later change is measured
against this table.
""")

code(r"""
print('=' * 64)
print('PHASE 0 BASELINE'.center(64))
print('=' * 64)
scorecard(LABELS, [not b for b in big],      'Stage 1  deskew')
scorecard(LABELS, [not b for b in paper_bad],'Stage 2  paper segmentation')
scorecard([LABELS[i] for i in inb], [not span_bad[i] for i in inb],
            'Stage 3  front-page boundary')
scorecard([LABELS[i] for i in fin], [not OVERTEXT[i] for i in fin],
            'Stage 4  finger fill (safe = not over inked content)')

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
             kind=r['scope'].kind, in_scope=r['scope'].in_scope,
             white=round(r['scope'].white_frac, 4),
             aspect=round(r['scope'].aspect, 3),
             span=round(SPAN[R.index(r)], 4),
             gutter=(r['bound'].gutter if r['bound'] else None),
             page_left=(r['bound'].left if r['bound'] else None),
             page_right=(r['bound'].right if r['bound'] else None),
             note=r['paper'].note, ms=round(r['ms'], 1)) for r in R]
with open('phase0_baseline.json', 'w') as fh:
    json.dump(rows, fh, indent=1)
print('\nwrote phase0_baseline.json')
""")

md(r"""
### Known open issues at end of Phase 0

Recorded here so they are not silently carried forward:

0. **Stage 3 open items.** Four captures of one dark magazine cover (#58-61) trim ~20%
   off the right edge -- the cover's own dark right side reads as a "darker plateau" and
   is mistaken for a fore-edge block. Five flat-lying spreads (#24, 26, 28, 35, 77) report
   no gutter, which is the honest answer: their deepest spine dip is 7-13 levels against
   a 13-level threshold. Boundaries on all five are correct.
0. **Closed books are now rejected up front** by the scope gate (#17, 18, 30–33), with
   zero valid pages wrongly skipped. Single-page documents stay in scope on a no-gutter
   path.
1. **Residual wall blob on ~6 images** (#109, #112, #117, #121–123). The occupancy trim
   removed it from the majority (#97–107, #113, #119, #124–130 are now clean), but where
   the blob overlaps the book's own row band it survives both the occupancy trim and
   `rect_fill` (min is now 0.832, so nothing flags). Needs a left-edge verticality test in
   Stage 3 — the book's true side edge is a long straight near-vertical line, the blob's is
   not.
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

# Carry over outputs from the previous build wherever the cell SOURCE is
# unchanged.  run_notebook.py writes rendered plots back into this file so the
# operator sees them in Jupyter; regenerating would otherwise blank every cell
# and make it look like nothing had ever run.  Cells whose source changed are
# deliberately left empty -- their old output no longer describes the new code.
carried = 0
if os.path.exists(out):
    try:
        with open(out) as fh:
            prev = json.load(fh)
        old = {}
        for c in prev.get('cells', []):
            if c.get('cell_type') == 'code' and c.get('outputs'):
                old[''.join(c['source'])] = (c['outputs'], c.get('execution_count'))
        for c in CELLS:
            if c['cell_type'] != 'code':
                continue
            hit = old.get(''.join(c['source']))
            if hit:
                c['outputs'], c['execution_count'] = hit
                carried += 1
    except (ValueError, KeyError):
        pass   # unreadable previous build: just write a clean notebook

with open(out, 'w') as fh:
    json.dump(nb, fh, indent=1)
print('wrote', out, f'({len(CELLS)} cells, {carried} with carried-over outputs)')
