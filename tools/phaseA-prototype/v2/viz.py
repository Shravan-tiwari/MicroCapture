"""
viz -- display helpers for the v2 notebook.

The whole point of this module: NO STAGE IS EVER JUDGED ON ONE PICTURE.

The previous prototype processed a single uploaded image, so every threshold in
it was tuned by looking at one photo -- which is exactly how it ended up with
parameters that fit one capture and broke on the next.  Every stage cell in the
v2 notebook therefore calls, for all 130 images:

    grid()        -- contact sheet of every image with the stage's overlay
    hist()        -- distribution of the stage's key metric
    show_flagged()-- the auto-flagged failures, shown LARGE with reasons
    by_class()    -- the same metric broken out per content class

so a change that helps one content type while breaking another is visible
immediately instead of averaging out.
"""

import math
import numpy as np
import cv2
import matplotlib.pyplot as plt
import matplotlib.gridspec as gridspec

from bookcv import CLASS_ORDER


def _rgb(img):
    if img.ndim == 2:
        return cv2.cvtColor(img, cv2.COLOR_GRAY2RGB)
    return cv2.cvtColor(img, cv2.COLOR_BGR2RGB)


def thumb(img, width=260):
    h, w = img.shape[:2]
    s = width / float(w)
    return cv2.resize(img, (width, max(1, int(round(h * s)))),
                      interpolation=cv2.INTER_AREA)


def grid(images, titles=None, cols=6, width=260, title="",
         flags=None, figsize_scale=1.0):
    """Contact sheet of EVERY image passed in.

    flags: optional list of bools; flagged tiles get a red border and a red
    title, so problem cases stand out while scrolling a 130-tile sheet.
    """
    n = len(images)
    if n == 0:
        print(f"[{title}] nothing to show")
        return
    rows = math.ceil(n / cols)
    fig_w = cols * 2.6 * figsize_scale
    fig_h = rows * 2.1 * figsize_scale
    fig, axes = plt.subplots(rows, cols, figsize=(fig_w, fig_h))
    axes = np.atleast_1d(axes).ravel()
    for i, ax in enumerate(axes):
        ax.axis('off')
        if i >= n:
            continue
        im = thumb(images[i], width)
        bad = bool(flags[i]) if flags is not None else False
        if bad:
            im = cv2.copyMakeBorder(im, 6, 6, 6, 6, cv2.BORDER_CONSTANT,
                                    value=(0, 0, 255))
        ax.imshow(_rgb(im))
        if titles is not None:
            ax.set_title(titles[i], fontsize=7,
                         color=('red' if bad else 'black'))
    if title:
        fig.suptitle(title, fontsize=13, y=1.002)
    plt.tight_layout()
    plt.show()


def show_flagged(images, titles, flags, reasons=None, cols=3, width=520,
                 title="FLAGGED", max_show=24):
    """Show only the flagged images, large enough to actually diagnose."""
    idx = [i for i, f in enumerate(flags) if f]
    if not idx:
        print(f"[{title}] none flagged -- all clear")
        return
    print(f"[{title}] {len(idx)} flagged: {[titles[i] for i in idx][:40]}")
    idx = idx[:max_show]
    sel = [images[i] for i in idx]
    tt = []
    for i in idx:
        t = titles[i]
        if reasons is not None and reasons[i]:
            t += f"\n{reasons[i]}"
        tt.append(t)
    grid(sel, tt, cols=cols, width=width, title=title, figsize_scale=1.7)


def hist(values, title="", xlabel="", bins=30, vlines=None):
    values = np.asarray(values, dtype=float)
    plt.figure(figsize=(11, 3.2))
    plt.hist(values, bins=bins, color='#4878a8', edgecolor='white')
    if vlines:
        for v, lab, c in vlines:
            plt.axvline(v, color=c, ls='--', lw=1.4, label=lab)
        plt.legend(fontsize=8)
    plt.title(title)
    plt.xlabel(xlabel)
    plt.grid(alpha=0.3)
    plt.tight_layout()
    plt.show()


def by_class(values, labels, title="", ylabel="", logy=False):
    """Box + strip plot of a metric grouped by content class.

    This is the plot that answers the operator's core concern directly: does this
    stage behave differently for text-heavy vs diagram-heavy vs image-heavy vs
    blank pages?
    """
    values = np.asarray(values, dtype=float)
    labels = list(labels)
    groups, names = [], []
    for c in CLASS_ORDER:
        v = values[[i for i, l in enumerate(labels) if l == c]]
        if len(v):
            groups.append(v)
            names.append(f"{c}\n(n={len(v)})")
    if not groups:
        return
    plt.figure(figsize=(11, 3.8))
    # matplotlib renamed boxplot's `labels` to `tick_labels` in 3.9; support both
    # so this runs on the operator's environment and on Kaggle alike.
    try:
        bp = plt.boxplot(groups, tick_labels=names, showfliers=False,
                         patch_artist=True)
    except TypeError:
        bp = plt.boxplot(groups, labels=names, showfliers=False,
                         patch_artist=True)
    for p in bp['boxes']:
        p.set_facecolor('#cfe0f0')
    for i, v in enumerate(groups):
        x = np.random.normal(i + 1, 0.055, len(v))
        plt.plot(x, v, '.', color='#c0392b', markersize=5, alpha=0.75)
    if logy:
        plt.yscale('log')
    plt.title(title)
    plt.ylabel(ylabel)
    plt.grid(alpha=0.3, axis='y')
    plt.tight_layout()
    plt.show()


def scorecard(labels, ok_flags, stage_name):
    """Pass rate per content class for one stage -- printed as a table."""
    labels = list(labels)
    ok = np.asarray(ok_flags, dtype=bool)
    print(f"\n=== {stage_name}: pass rate by content class ===")
    print(f"{'class':<16}{'n':>5}{'pass':>7}{'fail':>7}{'rate':>9}")
    total_ok = 0
    for c in CLASS_ORDER:
        ix = [i for i, l in enumerate(labels) if l == c]
        if not ix:
            continue
        k = int(ok[ix].sum())
        total_ok += k
        print(f"{c:<16}{len(ix):>5}{k:>7}{len(ix)-k:>7}{k/len(ix):>8.0%}")
    print(f"{'ALL':<16}{len(labels):>5}{total_ok:>7}"
          f"{len(labels)-total_ok:>7}{total_ok/max(len(labels),1):>8.0%}")


def overlay_mask(img, mask, color=(0, 0, 255), alpha=0.45):
    """Tint a binary mask over an image for visual checking."""
    out = img.copy()
    if mask is None:
        return out
    m = mask > 0
    layer = np.zeros_like(out)
    layer[:] = color
    out[m] = cv2.addWeighted(out, 1 - alpha, layer, alpha, 0)[m]
    return out


def draw_bbox(img, bbox, color=(0, 255, 0), t=3):
    out = img.copy()
    x, y, w, h = bbox
    cv2.rectangle(out, (x, y), (x + w, y + h), color, t)
    return out
