"""
bookcv -- classical CV stage library for the MicroCapture book-page pipeline (v2).

Design rules for everything in this file:

  1. PURE STAGES.  Every stage is a plain function taking arrays and returning a
     result object.  No plotting, no file IO, no globals.  The notebook does all
     display.  This is what lets a stage be run over all 130 images in a loop and
     then, separately, be visualised any way we like.

  2. CONTENT-INVARIANT EVIDENCE.  Measured on the real 130-image corpus, the
     |Gx| column profile that the previous pipeline traced is NOT separable from
     page content: on text-heavy pages the text body reaches 80-95 while the true
     page edge peaks at ~95 (644 columns rival the true edge on img 43).  So no
     stage here may assume "the page edge is the strongest gradient".  Stages key
     on REGION properties (brightness/uniformity of paper) which barely move when
     the printed content changes, and only then refine locally with gradients
     inside an already-constrained band.

  3. NEVER GUESS SILENTLY.  A stage that cannot find its feature returns
     found=False and says why in .note.  Downstream stages must handle that.
     Specifically: a single sheet has NO gutter and we must not invent one.

  4. EVERY STAGE IS SCORED.  Each result carries scalar metrics so the harness
     can flag suspicious images automatically instead of relying on eyeballing.
"""

from dataclasses import dataclass, field
from typing import Optional, List, Tuple
import numpy as np
import cv2


# ─────────────────────────── config ───────────────────────────

# All geometry work happens at this width.  The captures are 6316x4211 (26MP);
# tracing at full res is ~16x the pixels for no accuracy gain, since the features
# we key on (page edges, gutter, fingers) are tens of pixels wide.  Final warps
# are applied at full resolution by scaling the geometry back up.
WORK_W = 1600

# Corner patch size (px, at WORK_W) used to sample the copy-stand background level.
BG_PATCH = 40


def to_work(img: np.ndarray, work_w: int = WORK_W) -> Tuple[np.ndarray, float]:
    """Downscale to working width. Returns (image, scale) where
    scale = work_w / original_w, so geometry can be mapped back up."""
    h, w = img.shape[:2]
    if w <= work_w:
        return img.copy(), 1.0
    s = work_w / w
    return cv2.resize(img, (work_w, int(round(h * s))), interpolation=cv2.INTER_AREA), s


# ─────────────────────── stage 1: deskew ───────────────────────

@dataclass
class DeskewResult:
    angle: float                 # degrees applied (positive = counter-clockwise)
    rotated: np.ndarray          # deskewed image
    rect: tuple                  # cv2.minAreaRect of the book silhouette
    silhouette_frac: float       # book area / frame area -- sanity metric
    note: str = ""


def _book_silhouette(gray: np.ndarray) -> Optional[np.ndarray]:
    """Largest external contour of the book against the copy stand.

    Otsu is the right tool here and is content-invariant in the way we need: it
    separates the (bright, large) book from the (dark, large) stand.  What is
    printed on the page shifts the page's own histogram mass around a little but
    does not move the book/stand split, because paper stays far brighter than the
    stand even where it is densely printed.  Verified on all 130 (bg median 34,
    page median 221).
    """
    _, th = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    th = cv2.morphologyEx(th, cv2.MORPH_CLOSE, np.ones((25, 25), np.uint8))
    cnts, _ = cv2.findContours(th, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not cnts:
        return None
    return max(cnts, key=cv2.contourArea)


def _normalize_angle(angle: float) -> float:
    """Map minAreaRect's angle into (-45, 45].

    NOTE this is a SKEW corrector, not an orientation corrector: it cannot tell
    0 from 90 degrees.  A portrait page shot in landscape is left alone.  That is
    correct for this rig (fixed overhead camera, book always roughly landscape)
    but must not be mistaken for full orientation handling.
    """
    angle = angle % 90
    if angle > 45:
        angle -= 90
    return angle


def rotate_bound(img: np.ndarray, angle: float, border=(0, 0, 0)) -> np.ndarray:
    """Rotate about the centre, expanding the canvas so nothing is clipped."""
    h, w = img.shape[:2]
    cx, cy = w // 2, h // 2
    M = cv2.getRotationMatrix2D((cx, cy), angle, 1.0)
    cos, sin = abs(M[0, 0]), abs(M[0, 1])
    nW = int((h * sin) + (w * cos))
    nH = int((h * cos) + (w * sin))
    M[0, 2] += (nW / 2) - cx
    M[1, 2] += (nH / 2) - cy
    return cv2.warpAffine(img, M, (nW, nH), borderValue=border,
                          flags=cv2.INTER_LINEAR)


def deskew(img: np.ndarray) -> DeskewResult:
    """Stage 1 -- rotate the book upright.

    Ported from the operator's own `bookcurve (8).ipynb`, which ran 130/130 with
    zero errors (angle range -16.66..+15.33 deg, mean abs 2.68).  Why it beats
    the app's current text-line/Hough deskew: it keys on the BOOK SILHOUETTE, not
    on page content, so a diagram-heavy or blank page deskews exactly as well as
    a text page.  That is the same content-invariance principle as rule 2 above.

    Runs FIRST because every later stage traces along rows/columns and therefore
    assumes the book is axis-aligned.
    """
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    c = _book_silhouette(gray)
    if c is None:
        return DeskewResult(0.0, img.copy(), ((0, 0), (0, 0), 0), 0.0,
                            "no silhouette contour -- left unrotated")
    rect = cv2.minAreaRect(c)
    angle = _normalize_angle(rect[-1])
    frac = cv2.contourArea(c) / float(img.shape[0] * img.shape[1])
    rotated = rotate_bound(img, angle)
    note = ""
    if frac < 0.10:
        note = f"silhouette only {frac:.1%} of frame -- suspicious"
    elif frac > 0.92:
        note = f"silhouette {frac:.1%} of frame -- book may exceed frame"
    return DeskewResult(angle, rotated, rect, frac, note)


# ──────────────── stage 2: paper-region segmentation ────────────────

@dataclass
class PaperResult:
    mask: np.ndarray             # uint8 0/255, the paper region (both pages + block)
    bg_level: float
    paper_level: float
    contrast: float
    area_frac: float
    bbox: Tuple[int, int, int, int]   # x, y, w, h
    # Fraction of the mask's own minAreaRect that the mask fills.  An open book is
    # very nearly a filled rectangle (measured 0.81-0.95); a book with a wall blob
    # or a hand fused onto it is not.  This metric exists because area_frac ALONE
    # passed ~40 images whose masks were visibly contaminated -- the blob kept the
    # area inside the accepted window.  Shape catches what area cannot.
    rect_fill: float = 0.0
    found: bool = True
    note: str = ""


def segment_paper(img: np.ndarray) -> PaperResult:
    """Stage 2 -- segment paper from the copy stand.

    THIS IS THE KEY DESIGN CHANGE vs the old pipeline.  Instead of asking "where
    is the strongest vertical gradient" (which text defeats -- see module
    docstring), we ask "which pixels are paper".  Paper is bright and, crucially,
    stays bright where it is printed on: dense text lowers a neighbourhood's mean
    by a few tens of levels, while the paper-to-stand step is ~185 levels on this
    corpus.  So a brightness-based region test is nearly content-invariant, which
    is exactly the property the gradient test lacked.

    Robustness details that matter on the real corpus:
      * Threshold is derived from the image's OWN bg/paper levels rather than a
        fixed constant, because lighting falls off across the stand (measured:
        bg 12..60 across the 130).
      * Morphological close then open: close bridges text/gutter shadow so the
        page reads as one region; open removes stand specks (the rig's bright
        bolts) that would otherwise seed false regions.
      * Largest connected component only -- the book is one object.
    """
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    h, w = gray.shape

    p = BG_PATCH
    corners = np.concatenate([
        gray[:p, :p].ravel(), gray[:p, -p:].ravel(),
        gray[-p:, :p].ravel(), gray[-p:, -p:].ravel()])
    bg = float(np.median(corners))

    # Paper level: the bright mode of the image.  Use a high percentile rather
    # than Otsu's class mean so heavy ink doesn't drag the estimate down.
    paper = float(np.percentile(gray, 90))
    contrast = paper - bg
    note = ""

    # Threshold is anchored on the PAPER level, not on Otsu's split and not on an
    # offset from the corner background.  Both of those were tried on the real
    # corpus and both failed, in opposite directions:
    #
    #   * bg + k*contrast  -- images 90-121 are shot with a lit wall behind the
    #     stand.  Corner patches read ~47 (stand) while the frame median is ~150
    #     (wall), so a corner-anchored threshold sits under the wall and the wall
    #     floods in: paper fractions of 0.77-0.98 where the book covers ~half.
    #
    #   * Otsu -- better, but still lands at 126-148 on those same images while
    #     the wall itself sits at 90-118.  Visual check of all 130 (not the area
    #     metric, which happily passed them) showed a wall blob fused to the book
    #     in ~40 masks, and because it is CONNECTED to the book, "largest
    #     component" cannot drop it.
    #
    # Paper, however, is consistently ~220-240 while the wall is ~90-118: a gap of
    # 116+ levels on every image measured.  So key the threshold on the paper mode
    # and cut well below it but far above the wall.  This is the same
    # content-invariance argument as the module docstring: ink lowers a
    # neighbourhood mean by tens of levels, nothing like this gap.
    otsu_t = float(cv2.threshold(gray, 0, 255,
                                 cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0])

    if contrast < 25:
        # Black cover / very dark material (imgs 17, 18): brightness alone cannot
        # separate object from stand.  Otsu is all we have; flag it.
        _, th = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
        note = f"low contrast ({contrast:.0f}) -- dark cover, Otsu only"
    else:
        # 0.62 of the way from stand to paper.  Low enough to keep shadowed paper
        # near the gutter and the darker fore-edge block; high enough to exclude a
        # lit wall.  Also require the cut to clear Otsu, so on a plain dark-stand
        # image (where Otsu is already correct) we never threshold lower than it.
        t = max(bg + contrast * 0.62, otsu_t)
        _, th = cv2.threshold(gray, t, 255, cv2.THRESH_BINARY)

    th = cv2.morphologyEx(th, cv2.MORPH_CLOSE, np.ones((31, 31), np.uint8))
    th = cv2.morphologyEx(th, cv2.MORPH_OPEN, np.ones((15, 15), np.uint8))

    n, lab, stats, _ = cv2.connectedComponentsWithStats(th, 8)
    if n <= 1:
        return PaperResult(np.zeros_like(gray), bg, paper, contrast, 0.0,
                           (0, 0, 0, 0), 0.0, False, "no paper region found")
    k = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    mask = np.where(lab == k, 255, 0).astype(np.uint8)

    # Fill interior holes (dark photos on the page must not punch through).
    ff = mask.copy()
    pad = np.zeros((mask.shape[0] + 2, mask.shape[1] + 2), np.uint8)
    cv2.floodFill(ff, pad, (0, 0), 255)
    mask = mask | cv2.bitwise_not(ff)

    # ---- trim side blobs by column/row occupancy -------------------------
    # Raising the threshold shrank the lit-wall blob on images ~90-130 but did not
    # remove it: the wall is bright enough in places to survive any brightness cut
    # that still keeps shadowed paper, it is CONNECTED to the book so component
    # selection cannot drop it, and it is convex enough that rect_fill only caught
    # one of them.  So separate it structurally instead.
    #
    # The signature is unambiguous in the mask's own column-occupancy profile: a
    # wall/hand blob occupies ~0.17-0.19 of the column height, while a real book
    # column occupies ~0.7+.  Keep only the central run of columns (and then rows)
    # that clear a fraction of the profile's own peak -- so this adapts per image
    # rather than assuming a fixed page height.  A book is one solid horizontal
    # run by construction, so taking the run CONTAINING THE PEAK column can never
    # split a genuine spread.
    def _trim(m: np.ndarray, axis: int, keep_frac: float = 0.45) -> np.ndarray:
        occ = (m > 0).sum(axis=axis).astype(np.float32)
        if occ.max() <= 0:
            return m
        good = occ >= occ.max() * keep_frac
        if not good.any():
            return m
        peak = int(np.argmax(occ))
        lo = peak
        while lo > 0 and good[lo - 1]:
            lo -= 1
        hi = peak
        while hi < len(good) - 1 and good[hi + 1]:
            hi += 1
        out = np.zeros_like(m)
        if axis == 0:      # occ indexed by column -> keep columns lo..hi
            out[:, lo:hi + 1] = m[:, lo:hi + 1]
        else:              # occ indexed by row
            out[lo:hi + 1, :] = m[lo:hi + 1, :]
        return out

    trimmed = _trim(_trim(mask, axis=0), axis=1)
    if (trimmed > 0).sum() > 0.25 * max((mask > 0).sum(), 1):
        # Only accept the trim if it kept the bulk of the region; otherwise the
        # occupancy profile was not book-shaped and we keep the untrimmed mask
        # rather than silently discarding most of the page.
        mask = trimmed
    else:
        note = ((note + "; " if note else "")
                + "occupancy trim rejected (would remove >75% of mask)")

    x, y, bw, bh = cv2.boundingRect(mask)
    frac = float((mask > 0).mean())

    cnts, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if cnts:
        c = max(cnts, key=cv2.contourArea)
        (_, _), (rw, rh), _ = cv2.minAreaRect(c)
        rect_area = rw * rh
        rect_fill = float(cv2.contourArea(c) / rect_area) if rect_area > 0 else 0.0
    else:
        rect_fill = 0.0

    if frac < 0.08:
        note = (note + "; " if note else "") + f"paper only {frac:.1%} of frame"
    if rect_fill and rect_fill < 0.75:
        note = ((note + "; " if note else "")
                + f"mask fills only {rect_fill:.0%} of its rect -- blob attached?")
    return PaperResult(mask, bg, paper, contrast, frac, (x, y, bw, bh),
                       rect_fill, True, note)


# ───────────────── content classification (diagnostic) ─────────────────

@dataclass
class ContentStats:
    ink: float
    edge_density: float
    textiness: float
    colorfulness: float
    big_blob: float
    label: str


def classify_content(img: np.ndarray, paper_mask: np.ndarray) -> ContentStats:
    """Diagnostic ONLY -- never routes the algorithm.

    We deliberately do not branch the pipeline on content type: that would mean
    five code paths, five threshold sets, and a classifier whose own errors land
    exactly on the hard images.  Instead one content-invariant algorithm runs for
    everything and this label is used to GROUP the scorecard, so a change that
    helps text-heavy pages while breaking image-heavy ones is immediately visible
    instead of averaging out.
    """
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    ys, xs = np.where(paper_mask > 0)
    if len(xs) < 1000:
        roi = gray
        roi_sat = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)[:, :, 1]
    else:
        # Erode inward so the block/stand edge doesn't pollute content stats.
        er = cv2.erode(paper_mask, np.ones((41, 41), np.uint8))
        ys, xs = np.where(er > 0)
        if len(xs) < 1000:
            er = paper_mask
            ys, xs = np.where(er > 0)
        y0, y1, x0, x1 = ys.min(), ys.max(), xs.min(), xs.max()
        roi = gray[y0:y1, x0:x1]
        roi_sat = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)[y0:y1, x0:x1, 1]

    paper_level = float(np.median(roi))
    ink = float((roi < paper_level - 40).mean())

    edges = cv2.Canny(roi, 50, 150)
    edge_density = float(edges.mean() / 255.0)

    sob_y = cv2.Sobel(roi, cv2.CV_32F, 0, 1, ksize=3)
    rp = np.abs(sob_y).mean(axis=1)
    rp = rp - rp.mean()
    if rp.std() > 0 and len(rp) > 70:
        ac = np.correlate(rp, rp, 'full')[len(rp) - 1:]
        ac = ac / (ac[0] + 1e-9)
        seg = ac[8:60]
        textiness = float(seg.max()) if seg.size else 0.0
    else:
        textiness = 0.0

    colorfulness = float(roi_sat.mean())

    dark = (roi < paper_level - 60).astype(np.uint8)
    dark = cv2.morphologyEx(dark, cv2.MORPH_OPEN, np.ones((9, 9), np.uint8))
    nb, _, st, _ = cv2.connectedComponentsWithStats(dark, 8)
    big_blob = float(st[1:, cv2.CC_STAT_AREA].max() / roi.size) if nb > 1 else 0.0

    if ink < 0.04:
        label = 'EMPTY/BLANK'
    elif colorfulness > 45 or big_blob > 0.14:
        label = 'IMAGE-HEAVY'
    elif textiness > 0.60 and ink > 0.12:
        label = 'TEXT-HEAVY'
    elif big_blob > 0.05 or edge_density < 0.09:
        label = 'DIAGRAM/SPARSE'
    else:
        label = 'MIXED'

    return ContentStats(ink, edge_density, textiness, colorfulness, big_blob, label)


CLASS_ORDER = ['TEXT-HEAVY', 'DIAGRAM/SPARSE', 'IMAGE-HEAVY', 'MIXED', 'EMPTY/BLANK']


# ─────────────── difficulty probe (why a stage will struggle) ───────────────

@dataclass
class EdgeSignalStats:
    peak_to_interior: float      # >3 easy, <2 the old tracer cannot separate edge from content
    rival_columns: int           # columns rivalling the true edge's strength
    profile: np.ndarray


def edge_signal(img: np.ndarray) -> EdgeSignalStats:
    """Quantifies how badly page CONTENT competes with the page EDGE in the
    |Gx| column profile -- i.e. how hard this image is for a gradient-tracing
    detector.  This is the measurement that proved the operator's hypothesis:
    text-heavy median ratio 3.00 and image-heavy 2.92, versus 4.82 for
    diagram/sparse, with 2-4x the rival columns.  Kept in the pipeline as a
    per-image difficulty score so the scorecard can correlate failures with it.
    """
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    h, w = gray.shape
    gx = cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3)
    prof = np.abs(gx[int(h * 0.35):int(h * 0.65), :]).mean(axis=0)
    prof = np.convolve(prof, np.ones(9) / 9, 'same')
    mx = float(prof.max()) if prof.size else 0.0
    rivals = int((prof > 0.5 * mx).sum())
    interior = prof[int(w * 0.2):int(w * 0.8)]
    med_int = float(np.median(interior)) if interior.size else 0.0
    ptr = mx / (med_int + 1e-6)
    return EdgeSignalStats(ptr, rivals, prof)
