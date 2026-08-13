# %%
"""
Phase A prototype: detect the FRONT two-page spread's boundary as 5 curves
(TOP, BOTTOM, LEFT, RIGHT, GUTTER) — not the largest visible contour, not a
4-corner quad. Diagnostic-only, does not touch the C# app.

Algorithm summary (see /Users/shravantiwari/.claude/plans/fizzy-wondering-pascal.md
for the full spec this implements):

1. Illumination-normalize + Sobel edge map (magnitude + gx/gy for orientation).
2. Rough Otsu prior mask, with a bimodality check — if the histogram isn't
   clearly bimodal, the mask is treated as unreliable and its terms are
   softened rather than trusted blindly.
3. TOP/BOTTOM traced as a per-column dynamic-programming shortest path within
   the mask's own column range, from several candidate center seeds outward
   in both directions, minimizing a combined cost (edge strength, gradient
   orientation matching the curve's expected orientation, distance to the
   mask boundary, gradient-vs-mask-normal alignment, and a jump/smoothness
   penalty that approximates a curvature penalty).
4. LEFT/RIGHT traced as a per-row DP over the row range spanned by TOP/BOTTOM,
   pinned at both ends to TOP's and BOTTOM's own left/right endpoints so the
   topology (TOP connects LEFT/RIGHT, BOTTOM connects LEFT/RIGHT) holds by
   construction.
5. GUTTER traced as a per-row DP between TOP-center and BOTTOM-center
   (several candidate center-column seeds tried), using darkness + local
   ridge/symmetry + continuity, hard-bounded at every row by dense
   interpolated LEFT(y)/RIGHT(y) so it can never cross either outer edge.
6. Output: the original photo with ONLY these 5 curves drawn on top. Nothing
   else (no edge map, no mask, no candidate paths).

Honest caveat on the "curvature penalty": true second-derivative (slope
*change*) tracking would need a 3-point DP state (row, incoming row) at every
column, which is expensive in pure Python/NumPy. This prototype approximates
it with a jump-squared penalty (position jump² ), which still strongly
discourages sustained drift (worse than linear, cheaper than 3-point state)
but is not identical to a true curvature term. Flagged here rather than
silently presented as the exact thing requested.

This file is written as a sequence of "# %%" cells (VS Code Jupyter /
Interactive Window convention — Shift+Enter or "Run Cell" runs just one).
The function-definition cells near the top are the same production code the
CLI batch workflow uses (`python boundary_prototype.py --all-fixtures`); the
INTERACTIVE WALKTHROUGH section near the bottom re-runs that same logic one
step at a time for a single image, with a plot/overlay after every step —
use that section for inspection, don't call detect_five_curves() as one
black box when you're trying to see what's happening inside it.
"""

import sys
import os
import glob
import csv
import shutil
import argparse
import cv2
import numpy as np

# %%
# ----------------------------------------------------------------------------
# Step 0: load + normalize
# ----------------------------------------------------------------------------

def load_and_prepare(path, max_width=1600):
    img = cv2.imread(path)
    if img is None:
        raise ValueError(f"Could not read image: {path}")
    h, w = img.shape[:2]
    if w > max_width:
        scale = max_width / w
        img = cv2.resize(img, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    background = cv2.GaussianBlur(gray, (0, 0), sigmaX=35, sigmaY=35)
    normalized = cv2.divide(gray, background, scale=255)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    enhanced = clahe.apply(normalized)
    return img, gray, enhanced


def edge_maps(gray):
    g = cv2.GaussianBlur(gray, (5, 5), 0)
    gx = cv2.Sobel(g, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(g, cv2.CV_32F, 0, 1, ksize=3)
    mag = cv2.magnitude(gx, gy)
    mag_n = mag / (mag.max() + 1e-6)
    return gx, gy, mag_n


# %%
def skin_exclusion_mask(img, cr_low=130, cr_high=185, cb_low=80, cb_high=140,
                         max_inward_frac=0.15):
    """Detect skin-toned pixels via YCrCb chrominance ranges (same thresholds
    as ImageProcessor.cs SkinCrLow/High, SkinCbLow/High). Returns a binary
    mask where 255 = skin pixel to EXCLUDE, 0 = keep.

    Only skin blobs that touch the image border are considered fingers —
    interior skin-toned regions (e.g. a printed photo of a person) are left
    alone, exactly matching the C# TryRemoveFingers logic.

    Real bug found by direct inspection (Trapezoid_Image001, curve.jpeg): on
    real photos, this same YCrCb range also matches aged/cream book paper
    under warm lighting — not just skin. A finger blob that touches the
    border can bleed-merge with a large stretch of misclassified page paper
    into one connected component hundreds of pixels deep (Trapezoid_Image001:
    693px deep into a 1066px-tall frame; curve.jpeg: 964px deep — clearly not
    a real finger, which only ever overlaps a small strip near the edge it
    enters from). The old area-based filter only caught this by accident
    (rejecting the oversized blob outright on one side, while an
    under-threshold-by-luck merged blob on the other side got kept whole and
    inpainted page content along with the real fingertip).

    Fix: clip every border-touching blob to within max_inward_frac of
    whichever border(s) it touches BEFORE the area filter — a real finger's
    own footprint is unaffected by this (it never reaches that deep), while
    a false-positive page-paper region attached to it gets truncated back to
    a plausible fingertip size. The area filter below then operates on the
    clipped footprint, so it can no longer be fooled by how much extra page
    happened to get miscolored."""
    ycrcb = cv2.cvtColor(img, cv2.COLOR_BGR2YCrCb)
    skin = cv2.inRange(ycrcb, (0, cr_low, cb_low), (255, cr_high, cb_high))

    # Tight kernel so inpainting stays strictly on the finger without bleeding into the page
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    skin = cv2.dilate(skin, kernel, iterations=1)

    H, W = skin.shape
    edge_margin = 8
    max_inward_row = max(30, int(H * max_inward_frac))
    max_inward_col = max(30, int(W * max_inward_frac))

    n_labels, labels, stats, _ = cv2.connectedComponentsWithStats(skin, connectivity=8)
    rows = np.arange(H)[:, None]
    cols = np.arange(W)[None, :]

    result = np.zeros_like(skin)
    image_area = H * W
    min_area = image_area * 0.0005   # tiny noise floor
    max_area = image_area * 0.08     # a finger is never 8%+ of the frame

    for label in range(1, n_labels):
        x, y, w, h, area = stats[label]
        touches_top = y <= edge_margin
        touches_bottom = y + h >= H - edge_margin
        touches_left = x <= edge_margin
        touches_right = x + w >= W - edge_margin
        if not (touches_top or touches_bottom or touches_left or touches_right):
            continue

        blob = labels == label
        # Depth-from-border reachability mask: a pixel survives only if it's
        # within max_inward_* of one of THIS blob's own touched borders —
        # this is what actually clips a page-paper tail off a real fingertip.
        reachable = np.zeros((H, W), dtype=bool)
        if touches_top:
            reachable |= rows < max_inward_row
        if touches_bottom:
            reachable |= rows >= H - max_inward_row
        if touches_left:
            reachable |= cols < max_inward_col
        if touches_right:
            reachable |= cols >= W - max_inward_col

        clipped = (blob & reachable).astype(np.uint8) * 255
        # Keep only the sub-component(s) of the clipped blob still touching
        # the border (depth-clipping can otherwise leave an isolated island
        # near the opposite border if the original blob wrapped a corner).
        sub_n, sub_labels, sub_stats, _ = cv2.connectedComponentsWithStats(clipped, connectivity=8)
        for sub_label in range(1, sub_n):
            sx, sy, sw, sh, sarea = sub_stats[sub_label]
            if sarea < min_area or sarea > max_area:
                continue
            sub_touches = (sx <= edge_margin or sy <= edge_margin or
                           sx + sw >= W - edge_margin or sy + sh >= H - edge_margin)
            if sub_touches:
                result[sub_labels == sub_label] = 255

    result = cv2.dilate(result, kernel, iterations=1)
    return result


# %%
# ----------------------------------------------------------------------------
# Step 1: rough prior mask, with bimodality check + fallback
# ----------------------------------------------------------------------------

def _border_touch_fraction(mask):
    """For each of the 4 image edges, what fraction of that edge's pixels
    are foreground. A real page, shot with any backdrop margin, should not
    run solidly along an edge; background clutter (a cabinet, a wall, a desk
    edge) often does."""
    h, w = mask.shape
    top = np.mean(mask[0, :] > 0)
    bottom = np.mean(mask[h - 1, :] > 0)
    left = np.mean(mask[:, 0] > 0)
    right = np.mean(mask[:, w - 1] > 0)
    return top, bottom, left, right


def _significant_contours(mask_bin, min_area_frac=0.03):
    """All contours above a noise floor, not just the single largest — the
    real page is not guaranteed to be the largest connected region in either
    threshold polarity (confirmed real case: a background cabinet AND a
    background wall/desk region were both larger than the actual book in
    Trapezoid_Image001's Otsu split)."""
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9))
    m = cv2.morphologyEx(mask_bin, cv2.MORPH_CLOSE, k)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, k)
    contours, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    h, w = mask_bin.shape
    min_area = min_area_frac * h * w
    out = []
    for c in contours:
        area = cv2.contourArea(c)
        if area >= min_area:
            clean = np.zeros_like(mask_bin)
            cv2.drawContours(clean, [c], -1, 255, thickness=cv2.FILLED)
            out.append((clean, area))
    return out


def rough_prior_mask(enhanced):
    hist = cv2.calcHist([enhanced], [0], None, [256], [0, 256]).flatten()
    total = hist.sum()
    thresh, mask_normal = cv2.threshold(enhanced, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    t = int(thresh)
    w0 = hist[: t + 1].sum() / total
    w1 = 1 - w0
    if w0 <= 0 or w1 <= 0:
        score = 0.0
    else:
        m0 = (hist[: t + 1] * np.arange(t + 1)).sum() / (hist[: t + 1].sum() + 1e-6)
        m1 = (hist[t + 1 :] * np.arange(t + 1, 256)).sum() / (hist[t + 1 :].sum() + 1e-6)
        between_var = w0 * w1 * (m0 - m1) ** 2
        total_var = enhanced.astype(np.float64).var() + 1e-6
        score = between_var / total_var

    reliable = score > 0.35  # same bar as ImageProcessor.cs BrightnessSeparationMinScore

    # Otsu only tells you WHERE the split is, not WHICH side is the page, and
    # the real page is not guaranteed to be the single largest connected
    # region on either side. Real failure found by direct inspection
    # (Trapezoid_Image001): a bright metal cabinet at the frame's left edge
    # (33% of frame area) AND a background wall/desk region (26%) were BOTH
    # larger than the actual book (28%, ranked #2 within its own threshold
    # side) — picking only the single largest contour, from only one
    # threshold side, missed the real page entirely both times.
    # Fix: gather every significant contour (>3% of frame area) from BOTH
    # threshold polarities, and score each by (a) how much it clings to the
    # image border (a real page, shot with backdrop margin, mostly shouldn't;
    # background clutter often does) and (b) plausible page/spread aspect
    # ratio — reject extreme slivers. Pick the best-scoring candidate, not
    # just the biggest.
    mask_normal = mask_normal.astype(np.uint8)
    mask_inverted = (255 - mask_normal).astype(np.uint8)

    candidates = []
    for m in (mask_normal, mask_inverted):
        for clean, area in _significant_contours(m):
            touches = _border_touch_fraction(clean)
            border_score = sum(1 for f in touches if f > 0.6)
            ys, xs = np.where(clean > 0)
            bw, bh = xs.max() - xs.min() + 1, ys.max() - ys.min() + 1
            aspect = bw / bh
            # plausible single-page (~0.5-0.85) or two-page-spread (~1.1-1.9)
            # aspect ratio; a cabinet/wall sliver (0.76 in the real failure,
            # but spanning the FULL frame height unlike a real single page)
            # is only excluded by border_score, not aspect alone — aspect
            # here mainly rejects thin noise strips.
            aspect_ok = 0.4 <= aspect <= 2.2
            extent = area / (bw * bh)  # solidity: how fully the bbox is filled
            candidates.append((clean, area, border_score, aspect_ok, extent))

    if not candidates:
        return np.zeros_like(mask_normal), float(score), False

    # Real bug found by direct inspection (Trapezoid_Image003/004, single
    # close-up page shots): border_score alone let a tiny (~9% of frame)
    # noise blob with ZERO border touches beat the actual page (56%, but
    # legitimately touching the bottom edge — the photographer shot close
    # enough that the page runs off-frame at the bottom, which is normal for
    # a single-page close-up and must not be penalized the same way a
    # background cabinet spanning a full side edge was). Fix: only compare
    # border_score among candidates large enough to plausibly BE the page;
    # a real page (single or spread) occupies a large fraction of the frame
    # in every fixture checked, so a 15%-of-frame floor filters out noise
    # blobs before border-touch scoring ever gets to prefer them.
    h, w = mask_normal.shape
    plausible = [c for c in candidates if c[1] >= 0.15 * h * w]
    pool = plausible if plausible else candidates

    # fewest border-clings first, then aspect-plausible, then most SOLID
    # (area / bbox-area), not just largest raw area. Real bug found by direct
    # inspection (Trapezoid_Image001): a candidate covering just the white
    # pages (extent 0.96 — a near-solid rectangle) lost to a larger candidate
    # that had merged the pages with the visible black book cover and a
    # sliver of background (extent 0.66 — visibly raggeder/less solid),
    # because both tied on border_score/aspect_ok and the old tiebreak
    # preferred whichever was bigger. A real page is a solid, evenly-filled
    # rectangle; a background/cover-swallowing blob is generally raggeder
    # even when its raw area is bigger, so solidity is the more reliable
    # signal between two otherwise-tied candidates.
    pool.sort(key=lambda c: (c[2], not c[3], -c[4]))
    mask = pool[0][0]

    return mask, float(score), reliable


# %%
def mask_distance_and_normal(mask):
    edges = cv2.Canny(mask, 50, 150)
    inv = 255 - edges
    dist = cv2.distanceTransform(inv, cv2.DIST_L2, 5)
    ndx = cv2.Sobel(dist, cv2.CV_32F, 1, 0, ksize=3)
    ndy = cv2.Sobel(dist, cv2.CV_32F, 0, 1, ksize=3)
    nmag = np.sqrt(ndx ** 2 + ndy ** 2) + 1e-6
    nx, ny = ndx / nmag, ndy / nmag
    return dist, nx, ny


def normalize01(a, lo_pct=2, hi_pct=98):
    lo, hi = np.percentile(a, lo_pct), np.percentile(a, hi_pct)
    if hi <= lo:
        return np.zeros_like(a, dtype=np.float64)
    return np.clip((a.astype(np.float64) - lo) / (hi - lo), 0, 1)


# %%
# ----------------------------------------------------------------------------
# Step 2: local cost fields (Round-3 refinements: orientation + mask-normal terms)
# ----------------------------------------------------------------------------

def local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable, orientation="vertical", edge_type=None, center_ref=None, skin_mask=None):
    gmag = np.sqrt(gx ** 2 + gy ** 2) + 1e-6
    if orientation == "vertical":
        # TOP/BOTTOM favor a predominantly vertical gradient
        orient_align = np.abs(gy) / gmag
    else:
        # LEFT/RIGHT favor a predominantly horizontal gradient
        orient_align = np.abs(gx) / gmag

    dist_n = normalize01(dist)
    normal_align = np.abs((gx * nx + gy * ny) / gmag)

    w_edge, w_orient = 1.0, 0.6
    # if the prior mask wasn't reliably bimodal, soften its two terms so the
    # edge-based terms carry proportionally more weight (Otsu fallback, P2).
    w_dist = 0.5 if mask_reliable else 0.15
    w_normal = 0.4 if mask_reliable else 0.1

    cost = (
        w_edge * (1 - mag_n)
        + w_orient * (1 - orient_align)
        + w_dist * dist_n
        + w_normal * (1 - normal_align)
    )

    # Inward penetration penalty: strongly discourage DP from dipping inside the page
    if center_ref is not None and edge_type is not None:
        H, W = mag_n.shape
        w_inward = 1.5 if mask_reliable else 0.8
        if edge_type == "top":
            rows = np.arange(H)[:, None]
            inward_depth = np.maximum(0.0, rows - center_ref[None, :])
            cost += w_inward * (inward_depth / (H * 0.08)) ** 1.5
        elif edge_type == "bottom":
            rows = np.arange(H)[:, None]
            inward_depth = np.maximum(0.0, center_ref[None, :] - rows)
            cost += w_inward * (inward_depth / (H * 0.08)) ** 1.5
        elif edge_type == "left":
            cols = np.arange(W)[None, :]
            inward_depth = np.maximum(0.0, cols - center_ref[:, None])
            cost += w_inward * (inward_depth / (W * 0.08)) ** 1.5
        elif edge_type == "right":
            cols = np.arange(W)[None, :]
            inward_depth = np.maximum(0.0, center_ref[:, None] - cols)
            cost += w_inward * (inward_depth / (W * 0.08)) ** 1.5

    # NOTE: no additive cost penalty on skin-touching columns here — tried a flat
    # +5.0 bump and it backfired badly (confirmed on Trapezoid_Image001, whose
    # fingers wrap over the true top-left corner): with band_r ~130px, detouring
    # entirely around the finger's width was cheaper than crossing it, sending
    # TOP/LEFT ballooning into the black background rather than following the
    # real edge underneath/beside the finger. Skin robustness instead comes from
    # (a) inpainting the finger pixels before recomputing the edge/mask fields,
    # so the real edge signal is naturally restored under where the finger was,
    # and (b) the top/bottom-center interpolation across skin columns above,
    # which keeps the DP's search band anchored near the true edge through that
    # stretch. Both were kept; only this extra penalty was removed.

    return cost


# %%
# ----------------------------------------------------------------------------
# Step 3: generic 1D DP tracer (position + jump-squared "curvature" approx)
# ----------------------------------------------------------------------------

def dp_trace_1d(cost_map, axis, band_center, band_radius, w_jump=0.05, w_curv=0.02,
                 index_range=None, pin_start=None, pin_end=None):
    """
    axis='cols': trace one point per column (band over rows) -> TOP/BOTTOM.
    axis='rows': trace one point per row (band over columns)  -> LEFT/RIGHT/GUTTER.

    band_center[i]: expected center position (row or col) for lattice index i.
    index_range: (start, end) inclusive range of lattice indices to trace over.
    pin_start/pin_end: optional hard-fixed position at the first/last index.
    Returns (positions: np.ndarray over index_range, total_cost: float).
    """
    H, W = cost_map.shape
    limit = H if axis == "cols" else W
    lo_idx, hi_idx = index_range
    n = hi_idx - lo_idx + 1

    def band_at(k):
        c = int(band_center[k])
        lo = max(0, c - band_radius)
        hi = min(limit - 1, c + band_radius)
        return np.arange(lo, hi + 1)

    def cost_at(pos, k):
        return cost_map[pos, k] if axis == "cols" else cost_map[k, pos]

    positions_per_step = []
    for step in range(n):
        idx = lo_idx + step
        if step == 0 and pin_start is not None:
            positions_per_step.append(np.array([pin_start]))
        elif step == n - 1 and pin_end is not None:
            positions_per_step.append(np.array([pin_end]))
        else:
            positions_per_step.append(band_at(idx))

    prev_pos = positions_per_step[0]
    prev_cost = cost_at(prev_pos, lo_idx).astype(np.float64)
    backptr = [None] * n

    for step in range(1, n):
        idx = lo_idx + step
        pos = positions_per_step[step]
        diff = pos[:, None] - prev_pos[None, :]
        trans = w_jump * np.abs(diff) + w_curv * (diff.astype(np.float64) ** 2)
        total = trans + prev_cost[None, :]
        best_i = np.argmin(total, axis=1)
        best_v = total[np.arange(len(pos)), best_i]
        new_cost = best_v + cost_at(pos, idx)
        backptr[step] = prev_pos[best_i]
        prev_pos, prev_cost = pos, new_cost

    end_i = int(np.argmin(prev_cost))
    total_cost = float(prev_cost[end_i])
    path = [int(prev_pos[end_i])]
    pos = int(prev_pos[end_i])
    for step in range(n - 1, 0, -1):
        cand = positions_per_step[step]
        i = int(np.where(cand == pos)[0][0])
        pos = int(backptr[step][i])
        path.append(pos)
    path.reverse()
    return np.array(path), total_cost


def dp_trace_bidirectional_from_seed(cost_map, axis, band_center, band_radius,
                                      idx_lo, idx_hi, seed_idx, **kw):
    """Trace outward from seed_idx to idx_lo and to idx_hi, splice together."""
    right_path, right_cost = dp_trace_1d(
        cost_map, axis, band_center, band_radius,
        index_range=(seed_idx, idx_hi), **kw
    )
    left_path, left_cost = dp_trace_1d(
        cost_map, axis, band_center, band_radius,
        index_range=(idx_lo, seed_idx), **kw
    )
    full = np.concatenate([left_path[:-1], right_path])
    return full, left_cost + right_cost


def trace_with_seed_search(cost_map, axis, band_center, band_radius, idx_lo, idx_hi,
                            n_seeds=5, seed_frac=0.15, **kw):
    """Round-3 point 3: try several seed candidates near center, keep the
    lowest-total-cost path."""
    mid = (idx_lo + idx_hi) // 2
    span = int((idx_hi - idx_lo) * seed_frac)
    seeds = np.linspace(mid - span, mid + span, n_seeds).astype(int)
    seeds = np.clip(seeds, idx_lo, idx_hi)
    best = None
    for s in seeds:
        path, cost = dp_trace_bidirectional_from_seed(
            cost_map, axis, band_center, band_radius, idx_lo, idx_hi, int(s), **kw
        )
        if best is None or cost < best[1]:
            best = (path, cost, int(s))
    return best  # (path, cost, seed_used)


# %%
# ----------------------------------------------------------------------------
# Step 4: dense interpolation helper (Round-3: precompute LEFT(y)/RIGHT(y))
# ----------------------------------------------------------------------------

def dense_interp(path, idx_lo, idx_hi, full_len):
    """path is one value per index in [idx_lo, idx_hi]; return a full-length
    array (length full_len) with linear extrapolation-free clamping outside
    the known range (clamped to the nearest known endpoint)."""
    xs = np.arange(idx_lo, idx_hi + 1)
    out = np.interp(np.arange(full_len), xs, path, left=path[0], right=path[-1])
    return out


def find_v_shaped_contact_point(path, edge_type="top"):
    """Finds the exact (x_offset, y) V-shaped contact point where the two pages
    meet at the spine, using curvature analysis.

    The V-notch is the point of SHARPEST BEND (highest curvature) in the
    central span of the curve. This is robust to overall slope/tilt.

    For TOP curve: the V-notch dips DOWNWARD → local max in y → second
    derivative d²y/dx² is most NEGATIVE at the notch.

    For BOTTOM curve: the V-notch dips UPWARD → local min in y → second
    derivative d²y/dx² is most POSITIVE at the notch.

    Restricted to central 25%-75% span to avoid edge artifacts.
    """
    from scipy.ndimage import gaussian_filter1d

    n = len(path)
    lo_span = int(n * 0.35)
    hi_span = int(n * 0.65)
    if hi_span <= lo_span:
        lo_span, hi_span = 0, n - 1

    # Smooth to suppress text/noise — sigma proportional to image width
    sigma = max(15, n // 40)
    smoothed = gaussian_filter1d(path.astype(np.float64), sigma=sigma)

    # Second derivative (curvature proxy)
    d2 = np.gradient(np.gradient(smoothed))

    sub_d2 = d2[lo_span:hi_span + 1]
    if edge_type == "top":
        # Downward dip → local max in y → most negative second derivative
        rel_idx = int(np.argmin(sub_d2))
    else:
        # Upward dip → local min in y → most positive second derivative
        rel_idx = int(np.argmax(sub_d2))

    idx = lo_span + rel_idx
    return idx, int(path[idx])


# %%
# ----------------------------------------------------------------------------
# Step 5: gutter local cost (darkness + ridge/symmetry), combined with the
# shared DP's continuity/curvature terms via the same dp_trace_1d function.
# ----------------------------------------------------------------------------

def gutter_local_cost(gray, left_dense, right_dense, ridge_half_win=18):
    H, W = gray.shape
    g = gray.astype(np.float64)
    row_mean = cv2.blur(g, (1, 41))  # local horizontal smoothing per row-ish neighborhood
    cost = np.full((H, W), 5.0)  # high cost outside [left,right] band by default
    for y in range(H):
        lo = int(max(0, left_dense[y]))
        hi = int(min(W - 1, right_dense[y]))
        if hi - lo < 4:
            continue
        seg = g[y, lo : hi + 1]
        local_avg = row_mean[y, lo : hi + 1]
        darkness = normalize01(local_avg - seg)  # darker than local neighborhood -> higher score

        # ridge/symmetry: for each candidate x, compare mean intensity just
        # left vs just right of it; a real gutter has comparable page material
        # on both sides (low |left-right| diff), unlike a one-sided shadow/edge.
        w = ridge_half_win
        padded = np.pad(seg, w, mode="edge")
        cs = np.cumsum(np.insert(padded, 0, 0))
        left_sum = cs[w : w + len(seg)] - cs[0 : len(seg)]
        right_sum = cs[2 * w + 1 : 2 * w + 1 + len(seg)] - cs[w + 1 : w + 1 + len(seg)]
        left_avg = left_sum / w
        right_avg = right_sum / w
        asymmetry = normalize01(np.abs(left_avg - right_avg))
        # valley shape: center should be darker than both flanks
        valley = normalize01(np.maximum(0, ((left_avg + right_avg) / 2.0) - seg))

        local_cost = (
            1.0 * (1 - darkness)
            + 0.6 * asymmetry
            + 0.6 * (1 - valley)
        )
        cost[y, lo : hi + 1] = local_cost
    return cost


# %%
# ----------------------------------------------------------------------------
# Step 6: post-DP two-sided contrast check (diagnostic only, not a hard reject)
# ----------------------------------------------------------------------------

def contrast_consistency_score(gray, path, axis, offset=6):
    vals = []
    if axis == "cols":
        for x, y in enumerate(path):
            y0, y1 = max(0, y - offset), min(gray.shape[0] - 1, y + offset)
            side_a = float(np.mean(gray[max(0, y - offset - 3):max(1, y - offset), x])) if y - offset - 3 >= 0 else np.nan
            side_b = float(np.mean(gray[min(gray.shape[0]-1, y+offset):min(gray.shape[0], y+offset+3), x]))
            vals.append(side_b - side_a)
    else:
        for y, x in enumerate(path):
            side_a = float(np.mean(gray[y, max(0, x - offset - 3):max(1, x - offset)])) if x - offset - 3 >= 0 else np.nan
            side_b = float(np.mean(gray[y, min(gray.shape[1]-1, x+offset):min(gray.shape[1], x+offset+3)]))
            vals.append(side_b - side_a)
    vals = np.array([v for v in vals if not np.isnan(v)])
    if len(vals) == 0:
        return 0.0, 0.0
    polarity_consistency = abs(np.mean(np.sign(vals)))  # 1.0 = fully consistent sign
    mean_abs_transition = float(np.mean(np.abs(vals)))
    return polarity_consistency, mean_abs_transition


# %%
# ----------------------------------------------------------------------------
# Main detector — the production function the CLI batch workflow calls.
# For step-by-step inspection with a visualization after each step, use the
# INTERACTIVE WALKTHROUGH section near the bottom of this file instead of
# calling this as one black box.
# ----------------------------------------------------------------------------

def detect_five_curves(path, verbose=True):
    img, gray, enhanced = load_and_prepare(path)
    H, W = gray.shape

    # 1. Finger removal: detect skin-toned regions touching frame edge and inpaint them away
    skin_mask = skin_exclusion_mask(img)
    skin_pixels = np.count_nonzero(skin_mask)
    if skin_pixels > 0:
        if verbose:
            print(f"  SKIN: inpainting {skin_pixels} skin pixels ({100*skin_pixels/(H*W):.1f}% of frame)")
        img = cv2.inpaint(img, skin_mask, inpaintRadius=5, flags=cv2.INPAINT_TELEA)
        # Recompute gray and enhanced on the cleaned image
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        background = cv2.GaussianBlur(gray, (0, 0), sigmaX=35, sigmaY=35)
        normalized = cv2.divide(gray, background, scale=255)
        clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        enhanced = clahe.apply(normalized)

    # 2. Outer boundary detection
    gx, gy, mag_n = edge_maps(enhanced)
    mask, bimodality_score, mask_reliable = rough_prior_mask(enhanced)
    dist, nx, ny = mask_distance_and_normal(mask)


    if verbose:
        print(f"[{os.path.basename(path)}] size={W}x{H} bimodality={bimodality_score:.3f} "
              f"mask_reliable={mask_reliable}")

    # Column range: use only columns where the mask has SUBSTANTIAL vertical
    # extent, not just any nonzero pixel — a thin tendril/flare (shadow,
    # morphological-closing artifact) at the extreme edge of the mask should
    # not define the trace domain, since it drags the per-column band center
    # (below) to a nonsense position that the DP's bounded search can never
    # escape from.
    col_heights = (mask > 0).sum(axis=0).astype(np.float64)
    nonzero = col_heights[col_heights > 0]
    if len(nonzero) < 10:
        xmin, xmax = int(W * 0.05), int(W * 0.95)
    else:
        height_thresh = 0.5 * np.median(nonzero)
        substantial_cols = np.where(col_heights > height_thresh)[0]
        xmin, xmax = int(substantial_cols.min()), int(substantial_cols.max())
        # Real bug found by direct inspection (Trapezoid_Image003): the very
        # FIRST column to cross the threshold is itself often a transitional/
        # ragged one — here col_heights read 873 (barely above the 819
        # threshold) right at xmin, then jumped straight to a stable ~1800
        # just 10 columns later. That one ragged column became the anchor
        # for every later column's search band (via the median filter
        # below), and the DP's bounded search could never recover the ~900px
        # difference. Trim a small margin inward past the noisy transition
        # before it can become anyone's anchor.
        margin = max(8, int(W * 0.015))
        if xmax - xmin > 4 * margin:
            xmin, xmax = xmin + margin, xmax - margin

    # --- TOP / BOTTOM band centers from mask per column, with fallback ---
    top_center = np.full(W, int(H * 0.05), dtype=np.float64)
    bot_center = np.full(W, int(H * 0.95), dtype=np.float64)
    for x in range(xmin, xmax + 1):
        colmask = np.where(mask[:, x] > 0)[0]
        if len(colmask) > 0:
            top_center[x] = colmask.min()
            bot_center[x] = colmask.max()

    # If skin_mask removed top/bottom pixels at finger columns, interpolate across those columns
    # so top_center doesn't dip inward into the page text.
    has_skin_col = np.array([skin_mask[:, x].any() for x in range(W)])
    valid_cols = np.where((~has_skin_col) & (np.arange(W) >= xmin) & (np.arange(W) <= xmax))[0]
    if len(valid_cols) > 2:
        top_center[xmin:xmax + 1] = np.interp(np.arange(xmin, xmax + 1), valid_cols, top_center[valid_cols])
        bot_center[xmin:xmax + 1] = np.interp(np.arange(xmin, xmax + 1), valid_cols, bot_center[valid_cols])

    from scipy.ndimage import median_filter
    med_k = max(25, (int(W * 0.07) // 2) * 2 + 1)  # odd kernel size
    top_center[xmin:xmax + 1] = median_filter(top_center[xmin:xmax + 1], size=med_k, mode="nearest")
    bot_center[xmin:xmax + 1] = median_filter(bot_center[xmin:xmax + 1], size=med_k, mode="nearest")

    vert_cost_top = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable, orientation="vertical", edge_type="top", center_ref=top_center, skin_mask=skin_mask)
    vert_cost_bot = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable, orientation="vertical", edge_type="bottom", center_ref=bot_center, skin_mask=skin_mask)

    band_r = max(30, int(H * 0.08))

    top_path, top_cost, top_seed = trace_with_seed_search(
        vert_cost_top, "cols", top_center, band_r, xmin, xmax, w_jump=0.08, w_curv=0.08
    )
    bot_path, bot_cost, bot_seed = trace_with_seed_search(
        vert_cost_bot, "cols", bot_center, band_r, xmin, xmax, w_jump=0.08, w_curv=0.08
    )
    if verbose:
        print(f"  TOP:    cost={top_cost:.1f} seed_col={top_seed}")
        print(f"  BOTTOM: cost={bot_cost:.1f} seed_col={bot_seed}")

    # --- LEFT / RIGHT: row-wise DP, pinned to TOP/BOTTOM endpoints ---
    top_left_row, top_right_row = int(top_path[0]), int(top_path[-1])
    bot_left_row, bot_right_row = int(bot_path[0]), int(bot_path[-1])

    left_row_lo, left_row_hi = sorted((top_left_row, bot_left_row))
    right_row_lo, right_row_hi = sorted((top_right_row, bot_right_row))

    left_center = np.full(H, xmin)
    right_center = np.full(H, xmax)
    for y in range(max(0, min(left_row_lo, right_row_lo) - 5),
                   min(H, max(left_row_hi, right_row_hi) + 5)):
        rowmask = np.where(mask[y, :] > 0)[0]
        if len(rowmask) > 0:
            left_center[y] = rowmask.min()
            right_center[y] = rowmask.max()

    horiz_cost_left = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable, orientation="horizontal", edge_type="left", center_ref=left_center, skin_mask=skin_mask)
    horiz_cost_right = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable, orientation="horizontal", edge_type="right", center_ref=right_center, skin_mask=skin_mask)

    band_r_lr = max(20, int(W * 0.04))

    left_path, left_cost = dp_trace_1d(
        horiz_cost_left, "rows", left_center, band_r_lr,
        index_range=(left_row_lo, left_row_hi),
        pin_start=xmin, pin_end=xmin,
    )
    right_path, right_cost = dp_trace_1d(
        horiz_cost_right, "rows", right_center, band_r_lr,
        index_range=(right_row_lo, right_row_hi),
        pin_start=xmax, pin_end=xmax,
    )

    if verbose:
        print(f"  LEFT:   cost={left_cost:.1f} rows[{left_row_lo}:{left_row_hi}]")
        print(f"  RIGHT:  cost={right_cost:.1f} rows[{right_row_lo}:{right_row_hi}]")

    # --- dense LEFT(y)/RIGHT(y) precomputed BEFORE gutter DP (Round-3 P1) ---
    left_dense = dense_interp(left_path, left_row_lo, left_row_hi, H)
    right_dense = dense_interp(right_path, right_row_lo, right_row_hi, H)
    # clamp to be safely inside so gutter never touches the outer edges
    left_dense = left_dense + 3
    right_dense = right_dense - 3

    # --- GUTTER: darkness + ridge/symmetry + continuity, bounded by LEFT/RIGHT ---
    gutter_row_lo = int(min(top_left_row, top_right_row, bot_left_row, bot_right_row))
    gutter_row_hi = int(max(top_left_row, top_right_row, bot_left_row, bot_right_row))
    gutter_row_lo = max(0, min(gutter_row_lo, H - 2))
    gutter_row_hi = min(H - 1, max(gutter_row_hi, gutter_row_lo + 1))

    gcost = gutter_local_cost(gray, left_dense, right_dense)
    gutter_center = ((left_dense + right_dense) / 2.0).astype(int)

    # Real instruction from the user: the gutter must start/end at the actual
    # POINTY VERTEX of the TOP/BOTTOM curves (the notch where the two pages'
    # curvature meets at the spine), not at whatever row the geometric
    # horizontal-center column happens to land on — the spine isn't
    # guaranteed to sit at that exact column (perspective, uneven framing).
    # Find it directly: within the curve's own central region, the vertex is
    # the point that deviates most from a straight line drawn between that
    # curve's own two endpoints — this catches the notch regardless of which
    # direction it points (down for TOP in the case checked, but not assumed
    # as a hardcoded direction).
    # Contact points where two pages meet (V-shaped notches at TOP and BOTTOM)
    top_v_idx, top_v_y = find_v_shaped_contact_point(top_path, edge_type="top")
    bot_v_idx, bot_v_y = find_v_shaped_contact_point(bot_path, edge_type="bottom")

    top_v_col = xmin + top_v_idx
    bot_v_col = xmin + bot_v_idx

    gutter_row_lo = max(0, min(top_v_y, H - 2))
    gutter_row_hi = min(H - 1, max(bot_v_y, gutter_row_lo + 1))

    band_r_g = max(15, int(W * 0.03))
    gutter_path, gutter_cost, gutter_seed = trace_with_seed_search(
        gcost, "rows", gutter_center, band_r_g, gutter_row_lo, gutter_row_hi,
        n_seeds=5, seed_frac=0.1,
    )

    # --- Gutter existence gate ---
    aspect_ratio = W / H
    gutter_present = aspect_ratio > 1.15

    if verbose:
        print(f"  GUTTER: V-top=({top_v_col}, {top_v_y}) V-bot=({bot_v_col}, {bot_v_y}) "
              f"aspect_ratio={aspect_ratio:.2f} present={gutter_present}")

    if gutter_present:
        # Connect top V-notch to bottom V-notch directly with a line
        num_rows = gutter_row_hi - gutter_row_lo + 1
        rows_arr = np.arange(gutter_row_lo, gutter_row_hi + 1)

        # Direct line from (top_v_col, top_v_y) to (bot_v_col, bot_v_y)
        if top_v_y != bot_v_y:
            slope = (bot_v_col - top_v_col) / float(bot_v_y - top_v_y)
            gutter_cols = top_v_col + slope * (rows_arr - top_v_y)
        else:
            gutter_cols = np.full(num_rows, top_v_col)

        gutter_path = np.round(gutter_cols).astype(int)

        # Ensure GUTTER stays bounded between LEFT and RIGHT
        for i, y in enumerate(rows_arr):
            lo, hi = left_dense[y], right_dense[y]
            gutter_path[i] = int(np.clip(gutter_path[i], lo + 2, hi - 2))

    # --- diagnostic-only contrast checks (printed, not blocking) ---
    top_pol, top_trans = contrast_consistency_score(gray, top_path, "cols")
    bot_pol, bot_trans = contrast_consistency_score(gray, bot_path, "cols")
    left_pol, left_trans = contrast_consistency_score(gray, left_path, "rows")
    right_pol, right_trans = contrast_consistency_score(gray, right_path, "rows")
    if verbose:
        print(f"  contrast-consistency (polarity 0-1, mean |transition|):")
        print(f"    TOP    {top_pol:.2f} / {top_trans:.1f}")
        print(f"    BOTTOM {bot_pol:.2f} / {bot_trans:.1f}")
        print(f"    LEFT   {left_pol:.2f} / {left_trans:.1f}")
        print(f"    RIGHT  {right_pol:.2f} / {right_trans:.1f}")

    curves = {
        "top": (np.arange(xmin, xmax + 1), top_path),
        "bottom": (np.arange(xmin, xmax + 1), bot_path),
        "left": (left_path, np.arange(left_row_lo, left_row_hi + 1)),
        "right": (right_path, np.arange(right_row_lo, right_row_hi + 1)),
    }
    if gutter_present:
        curves["gutter"] = (gutter_path, np.arange(gutter_row_lo, gutter_row_hi + 1))

    gutter_relative_improvement = 0.0
    gutter_cost = 0.0

    diagnostics = {
        "bimodality_score": bimodality_score,
        "mask_reliable": mask_reliable,
        "top_cost": top_cost, "bottom_cost": bot_cost,
        "left_cost": left_cost, "right_cost": right_cost, "gutter_cost": gutter_cost,
        "gutter_present": gutter_present, "aspect_ratio": aspect_ratio,
        "gutter_relative_improvement": gutter_relative_improvement,
        "contrast": {
            "top": (top_pol, top_trans), "bottom": (bot_pol, bot_trans),
            "left": (left_pol, left_trans), "right": (right_pol, right_trans),
        },
        "left_dense": left_dense, "right_dense": right_dense,
    }
    return img, curves, diagnostics


# %%
def draw_overlay(img, curves, out_path):
    vis = img.copy()
    colors = {
        "top": (0, 0, 255),      # red
        "bottom": (255, 0, 0),   # blue
        "left": (0, 200, 0),     # green
        "right": (0, 165, 255),  # orange
        "gutter": (255, 0, 255), # magenta
    }
    for name, (xs, ys) in curves.items():
        pts = np.array(list(zip(xs, ys)), dtype=np.int32)
        cv2.polylines(vis, [pts], isClosed=False, color=colors[name], thickness=3)
    cv2.imwrite(out_path, vis)
    print(f"  wrote {out_path}")


def draw_overlay_only(img, curves):
    """Same drawing as draw_overlay but returns the Mat instead of writing it,
    for reuse in the contact sheet."""
    vis = img.copy()
    colors = {
        "top": (0, 0, 255), "bottom": (255, 0, 0), "left": (0, 200, 0),
        "right": (0, 165, 255), "gutter": (255, 0, 255),
    }
    for name, (xs, ys) in curves.items():
        pts = np.array(list(zip(xs, ys)), dtype=np.int32)
        cv2.polylines(vis, [pts], isClosed=False, color=colors[name], thickness=3)
    return vis


# %%
# ----------------------------------------------------------------------------
# Batch workflow: all fixtures, one command, reproducible. This section is
# meant for the terminal (`python boundary_prototype.py --all-fixtures`), not
# for stepping through cell-by-cell — it processes every fixture in one call
# and writes files to disk rather than displaying anything inline. Use the
# INTERACTIVE WALKTHROUGH section further down for cell-by-cell inspection.
# ----------------------------------------------------------------------------

FIXTURES_ROOT = "/Users/shravantiwari/Micrographics/MicroCapture/tools/SmokeTest/Fixtures/real-photos"
# Only real books/magazines — calibration checkerboard photos aren't a
# "front two pages" case at all, so they're excluded from this test set
# per explicit instruction, not just skipped opportunistically.
FIXTURE_SUBDIRS = ["book-curve", "trapezoid"]
EXTRA_IMAGES = [os.path.expanduser("~/Downloads/curve.jpeg")]
# The difficult real photo where the underlying page stack is visible past
# the front pages' own left/right edges — the primary Phase A gating case
# per the plan (/Users/shravantiwari/.claude/plans/fizzy-wondering-pascal.md).
PRIMARY_DIFFICULT_IMAGE = "IMG_0022.JPG"


def list_all_fixtures():
    paths = set()
    for sub in FIXTURE_SUBDIRS:
        for ext in ("jpg", "JPG", "jpeg", "JPEG"):
            paths.update(glob.glob(os.path.join(FIXTURES_ROOT, sub, f"*.{ext}")))
    paths = sorted(paths)
    for p in EXTRA_IMAGES:
        if os.path.exists(p) and p not in paths:
            paths.append(p)
    return paths


def compute_statuses(gray_shape, curves, diagnostics):
    """Automated diagnostic flags ONLY — not proof of correctness. Per the
    plan and the user's own instruction: PASS/FAIL/FLAG here are heuristics
    to direct visual attention, never a substitute for actually looking at
    the overlay. Thresholds are simple and documented, not tuned per-image.
    """
    statuses = {}

    def edge_status(name):
        pol, trans = diagnostics["contrast"][name]
        if trans < 3.0:
            return "FLAG"  # near-zero two-sided transition -> edge barely evidenced
        return "OK" if pol >= 0.6 else "FLAG"

    for name in ("top", "bottom", "left", "right"):
        statuses[name] = edge_status(name)

    if not diagnostics.get("gutter_present", True):
        # No gutter was drawn at all — a deliberate decision (single-page
        # shot, no real spine found), not a detection failure. NONE is a
        # distinct diagnostic value: don't conflate it with FLAG/FAIL, and
        # don't score topology against a curve that doesn't exist.
        statuses["gutter"] = "NONE"
        statuses["topology"] = "N/A"
    else:
        n_gutter = len(curves["gutter"][1])
        gutter_cost_per_row = diagnostics["gutter_cost"] / max(1, n_gutter)
        statuses["gutter"] = "OK" if gutter_cost_per_row < 1.4 else "FLAG"

        # topology: GUTTER strictly between LEFT(y) and RIGHT(y) at every row
        # it was traced over, with a real (not zero) margin on both sides.
        H = gray_shape[0]
        left_dense, right_dense = diagnostics["left_dense"], diagnostics["right_dense"]
        gxs, gys = curves["gutter"]
        ok_topology = True
        for x, y in zip(gxs, gys):
            y = int(np.clip(y, 0, H - 1))
            if not (left_dense[y] + 1 < x < right_dense[y] - 1):
                ok_topology = False
                break
        statuses["topology"] = "OK" if ok_topology else "FAIL"

    vals = list(statuses.values())
    if "FAIL" in vals:
        overall = "FAIL"
    elif "FLAG" in vals:
        overall = "FLAG"
    else:
        overall = "PASS"
    statuses["overall"] = overall
    return statuses


def build_contact_sheet(entries, out_path, thumb_w=380, cols=4, label_h=34):
    """entries: list of (label, overlay_bgr_image_or_None, is_primary).
    Row heights are computed PER ROW (max thumbnail height within that row
    only), not globally — a single portrait-oriented fixture must not force
    every other row to carry its height as blank whitespace, which made the
    first version of this sheet very hard to scan."""
    n = len(entries)
    rows = (n + cols - 1) // cols

    thumbs = []
    for label, vis, is_primary in entries:
        if vis is None:
            thumb = np.full((int(thumb_w * 0.7), thumb_w, 3), 40, dtype=np.uint8)
            cv2.putText(thumb, "ERROR", (10, thumb.shape[0] // 2), cv2.FONT_HERSHEY_SIMPLEX,
                        0.8, (0, 0, 255), 2)
        else:
            h, w = vis.shape[:2]
            scale = thumb_w / w
            thumb = cv2.resize(vis, (thumb_w, int(h * scale)))
        thumbs.append(thumb)

    row_heights = []
    for r in range(rows):
        row_thumbs = thumbs[r * cols : (r + 1) * cols]
        row_heights.append(max(t.shape[0] for t in row_thumbs))
    row_y0 = [0]
    for r in range(rows):
        row_y0.append(row_y0[-1] + row_heights[r] + label_h)

    canvas = np.full((row_y0[-1], thumb_w * cols, 3), 255, dtype=np.uint8)

    for i, ((label, vis, is_primary), thumb) in enumerate(zip(entries, thumbs)):
        r, c = divmod(i, cols)
        y0 = row_y0[r]
        x0 = c * thumb_w
        cell_h = row_heights[r] + label_h
        border = (0, 0, 255) if is_primary else (200, 200, 200)
        cv2.rectangle(canvas, (x0, y0), (x0 + thumb_w - 1, y0 + cell_h - 1), border,
                      3 if is_primary else 1)
        canvas[y0 : y0 + thumb.shape[0], x0 : x0 + thumb.shape[1]] = thumb
        text = label if not is_primary else f"[PRIMARY] {label}"
        font_scale = 0.42
        cv2.putText(canvas, text, (x0 + 6, y0 + row_heights[r] + 22), cv2.FONT_HERSHEY_SIMPLEX,
                    font_scale, (0, 0, 0), 1, cv2.LINE_AA)

    cv2.imwrite(out_path, canvas)
    return out_path


def run_all_fixtures(base_out_dir):
    current_dir = os.path.join(base_out_dir, "current")
    previous_dir = os.path.join(base_out_dir, "previous")

    # Never overwrite the previously-approved result silently: rotate
    # current -> previous before writing a fresh current.
    if os.path.isdir(current_dir):
        if os.path.isdir(previous_dir):
            shutil.rmtree(previous_dir)
        shutil.move(current_dir, previous_dir)
    os.makedirs(os.path.join(current_dir, "individual"), exist_ok=True)

    paths = list_all_fixtures()
    print(f"Found {len(paths)} fixture images.\n")

    csv_rows = []
    contact_entries = []
    summary_lines = []

    for path in paths:
        base = os.path.splitext(os.path.basename(path))[0]
        is_primary = os.path.basename(path) == PRIMARY_DIFFICULT_IMAGE
        try:
            img, curves, diag = detect_five_curves(path, verbose=False)
            statuses = compute_statuses(img.shape, curves, diag)
            vis = draw_overlay_only(img, curves)
            out_png = os.path.join(current_dir, "individual", f"{base}_overlay.png")
            cv2.imwrite(out_png, vis)
            row = {
                "image": os.path.basename(path),
                "top_status": statuses["top"],
                "bottom_status": statuses["bottom"],
                "left_status": statuses["left"],
                "right_status": statuses["right"],
                "gutter_status": statuses["gutter"],
                "topology_status": statuses["topology"],
                "overall_status": statuses["overall"],
                "is_primary_difficult_case": is_primary,
            }
            line = (f"{base}: TOP={statuses['top']} BOTTOM={statuses['bottom']} "
                    f"LEFT={statuses['left']} RIGHT={statuses['right']} "
                    f"GUTTER={statuses['gutter']} TOPOLOGY={statuses['topology']} "
                    f"OVERALL={statuses['overall']}" + ("  <-- PRIMARY DIFFICULT CASE" if is_primary else ""))
            print(line)
            summary_lines.append(line)
            contact_entries.append((os.path.basename(path), vis, is_primary))
        except Exception as e:
            print(f"{base}: ERROR ({e})")
            row = {
                "image": os.path.basename(path),
                "top_status": "ERROR", "bottom_status": "ERROR", "left_status": "ERROR",
                "right_status": "ERROR", "gutter_status": "ERROR", "topology_status": "ERROR",
                "overall_status": "ERROR", "is_primary_difficult_case": is_primary,
            }
            summary_lines.append(f"{base}: ERROR ({e})")
            contact_entries.append((os.path.basename(path), None, is_primary))
        csv_rows.append(row)

    csv_path = os.path.join(current_dir, "phaseA_results.csv")
    with open(csv_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(csv_rows[0].keys()))
        writer.writeheader()
        writer.writerows(csv_rows)

    sheet_path = os.path.join(current_dir, "phaseA_contact_sheet.png")
    build_contact_sheet(contact_entries, sheet_path)

    n_pass = sum(1 for r in csv_rows if r["overall_status"] == "PASS")
    n_flag = sum(1 for r in csv_rows if r["overall_status"] == "FLAG")
    n_fail = sum(1 for r in csv_rows if r["overall_status"] in ("FAIL", "ERROR"))
    print(f"\n{len(csv_rows)} images: {n_pass} PASS-flagged, {n_flag} FLAG, {n_fail} FAIL/ERROR "
          f"(these are automated diagnostic flags, NOT a visual-correctness verdict — "
          f"the actual pass/fail bar is your own inspection of the contact sheet).")
    print(f"\nWrote:\n  {csv_path}\n  {sheet_path}\n  {os.path.join(current_dir, 'individual')}/ "
          f"({len(csv_rows)} overlay PNGs)")
    if os.path.isdir(previous_dir):
        print(f"\nPrevious run preserved at: {previous_dir}")


# %%
# ----------------------------------------------------------------------------
# CLI entry point. This cell is for `python boundary_prototype.py ...` from a
# terminal ONLY — do not run it as a cell in the VS Code Interactive Window /
# Jupyter (argparse will choke on the kernel's own argv). Use the INTERACTIVE
# WALKTHROUGH section below for interactive work.
# ----------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("images", nargs="*", help="specific image paths to process")
    parser.add_argument("--all-fixtures", action="store_true",
                         help="run all 17 fixtures (16 repo fixtures + ~/Downloads/curve.jpeg), "
                              "write phaseA_results/current/{individual/,phaseA_contact_sheet.png,"
                              "phaseA_results.csv}, rotating any existing current/ to previous/")
    args = parser.parse_args()

    if args.all_fixtures:
        out_dir = os.path.join(os.path.dirname(__file__), "phaseA_results")
        run_all_fixtures(out_dir)
    elif args.images:
        out_dir = os.path.join(os.path.dirname(__file__), "boundary_overlays")
        os.makedirs(out_dir, exist_ok=True)
        for p in args.images:
            img, curves, diag = detect_five_curves(p)
            base = os.path.splitext(os.path.basename(p))[0]
            draw_overlay(img, curves, os.path.join(out_dir, f"{base}_overlay.png"))
    else:
        parser.print_help()
        sys.exit(1)

    # Stop here for real script execution (`python boundary_prototype.py ...`)
    # — everything below is the INTERACTIVE WALKTHROUGH section, meant to be
    # run cell-by-cell in VS Code/Jupyter, not as part of a plain CLI run
    # (it imports matplotlib, which the batch/CLI path doesn't need).
    sys.exit(0)


# %%
# ============================================================================
# INTERACTIVE WALKTHROUGH
#
# Everything below re-runs detect_five_curves()'s own logic for ONE image,
# one small step at a time, with a plot/overlay after every meaningful step
# so you can inspect the result before moving to the next cell. It calls the
# exact same helper functions defined above (load_and_prepare, edge_maps,
# skin_exclusion_mask, rough_prior_mask, local_cost_field, dp_trace_1d, ...)
# in the exact same order detect_five_curves() does — this is NOT a separate
# algorithm, it's that same function unrolled into cells for inspection.
#
# Run cells top-to-bottom with Shift+Enter (or "Run Cell" in VS Code). Change
# IMAGE_PATH in the next cell and re-run from there to try a different
# fixture. Don't skip ahead — each cell depends on variables from the ones
# above it, same as detect_five_curves()'s own internal order.
# ============================================================================

import matplotlib.pyplot as plt

try:
    get_ipython().run_line_magic("matplotlib", "inline")
except NameError:
    pass  # not running under IPython/Jupyter — plt.show() below still works


def show(img_bgr, title="", figsize=(10, 7)):
    """Display a BGR OpenCV image inline."""
    rgb = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2RGB)
    plt.figure(figsize=figsize)
    plt.imshow(rgb)
    plt.title(title)
    plt.axis("off")
    plt.show()


def show_gray(arr, title="", cmap="viridis", figsize=(10, 7)):
    """Display a single-channel array (mask/cost-field/heatmap) inline, with a colorbar."""
    plt.figure(figsize=figsize)
    im = plt.imshow(arr, cmap=cmap)
    plt.colorbar(im, fraction=0.03)
    plt.title(title)
    plt.axis("off")
    plt.show()


def show_overlay_mask(img_bgr, mask, color=(0, 0, 255), alpha=0.5, title="", figsize=(10, 7)):
    """Overlay a binary mask (nonzero -> highlighted) on top of a BGR image."""
    vis = img_bgr.copy()
    m = mask > 0
    color_arr = np.array(color, dtype=np.float64)
    vis[m] = (vis[m].astype(np.float64) * (1 - alpha) + color_arr * alpha).astype(np.uint8)
    show(vis, title, figsize=figsize)


def show_curve(img_bgr, xs, ys, color=(0, 0, 255), title="", extra=None, figsize=(10, 7)):
    """Draw one traced curve (x,y arrays) on the image, optionally with more
    curves layered on top (extra = list of (xs, ys, color) tuples), and show it."""
    vis = img_bgr.copy()
    pts = np.array(list(zip(xs, ys)), dtype=np.int32)
    cv2.polylines(vis, [pts], isClosed=False, color=color, thickness=3)
    if extra:
        for exs, eys, ecolor in extra:
            epts = np.array(list(zip(exs, eys)), dtype=np.int32)
            cv2.polylines(vis, [epts], isClosed=False, color=ecolor, thickness=3)
    show(vis, title, figsize=figsize)


# %%
# Pick the fixture to walk through. FIXTURES_ROOT is defined in the batch
# workflow cell above.
IMAGE_PATH = os.path.join(FIXTURES_ROOT, "trapezoid", "Trapezoid_Image001.JPG")
print("Using:", IMAGE_PATH)

# %%
# 1. Load + resize-for-processing + illumination-normalize.
img, gray, enhanced = load_and_prepare(IMAGE_PATH)
H, W = gray.shape
print(f"size: {W}x{H}")
show(img, "1. Loaded + resized (raw)")
show_gray(gray, "1. Grayscale")
show_gray(enhanced, "1. Illumination-normalized + CLAHE")

# %%
# 2. Sobel gradient magnitude/orientation on the normalized image.
gx, gy, mag_n = edge_maps(enhanced)
show_gray(mag_n, "2. Sobel gradient magnitude (normalized)")

# %%
# 3. Skin/finger detection — should hug ONLY real fingers, not page paper.
skin_mask = skin_exclusion_mask(img)
skin_pixels = int(np.count_nonzero(skin_mask))
print(f"skin pixels: {skin_pixels} ({100*skin_pixels/(H*W):.2f}% of frame)")
show_overlay_mask(img, skin_mask, color=(0, 0, 255),
                   title="3. Skin/finger mask (red) — check it doesn't bleed into the page")

# %%
# 4. Inpaint the finger pixels away. Check visually that no page CONTENT was
# eaten — only the finger(s).
if skin_pixels > 0:
    img_clean = cv2.inpaint(img, skin_mask, inpaintRadius=5, flags=cv2.INPAINT_TELEA)
else:
    img_clean = img.copy()
show(img_clean, "4. After finger inpainting")

# %%
# 5. Recompute gray/enhanced/gradients on the cleaned image (same as
# detect_five_curves does once inpainting has happened).
gray_clean = cv2.cvtColor(img_clean, cv2.COLOR_BGR2GRAY)
background = cv2.GaussianBlur(gray_clean, (0, 0), sigmaX=35, sigmaY=35)
normalized = cv2.divide(gray_clean, background, scale=255)
clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
enhanced_clean = clahe.apply(normalized)
gx, gy, mag_n = edge_maps(enhanced_clean)
show_gray(mag_n, "5. Gradient magnitude, recomputed on the cleaned image")

# %%
# 6. Rough Otsu prior mask — should hug just the white pages, not the book
# cover or background. This is the step that picks WHICH candidate region is
# "the page" (see rough_prior_mask's solidity tiebreak).
mask, bimodality_score, mask_reliable = rough_prior_mask(enhanced_clean)
print(f"bimodality={bimodality_score:.3f} reliable={mask_reliable}")
show_overlay_mask(img_clean, mask, color=(0, 255, 0),
                   title="6. Rough prior mask (green) — should be just the white pages")

# %%
# 7. Distance-to-mask-boundary field, used by local_cost_field to keep traced
# curves anchored near the mask's own edge.
dist, nx, ny = mask_distance_and_normal(mask)
show_gray(dist, "7. Distance-to-mask-boundary field")

# %%
# 8. Column range the TOP/BOTTOM DP trace will search over.
col_heights = (mask > 0).sum(axis=0).astype(np.float64)
nonzero = col_heights[col_heights > 0]
if len(nonzero) < 10:
    xmin, xmax = int(W * 0.05), int(W * 0.95)
else:
    height_thresh = 0.5 * np.median(nonzero)
    substantial_cols = np.where(col_heights > height_thresh)[0]
    xmin, xmax = int(substantial_cols.min()), int(substantial_cols.max())
    margin = max(8, int(W * 0.015))
    if xmax - xmin > 4 * margin:
        xmin, xmax = xmin + margin, xmax - margin
print(f"column range used for TOP/BOTTOM: [{xmin}, {xmax}]")

vis = img_clean.copy()
cv2.line(vis, (xmin, 0), (xmin, H - 1), (255, 255, 0), 2)
cv2.line(vis, (xmax, 0), (xmax, H - 1), (255, 255, 0), 2)
show(vis, "8. Trace column range (cyan lines)")

# %%
# 9. TOP/BOTTOM search-band CENTERS (anchors the DP searches around — not the
# final traced path yet), with skin-column interpolation + median-filter
# smoothing already applied.
top_center = np.full(W, int(H * 0.05), dtype=np.float64)
bot_center = np.full(W, int(H * 0.95), dtype=np.float64)
for x in range(xmin, xmax + 1):
    colmask = np.where(mask[:, x] > 0)[0]
    if len(colmask) > 0:
        top_center[x] = colmask.min()
        bot_center[x] = colmask.max()

has_skin_col = np.array([skin_mask[:, x].any() for x in range(W)])
valid_cols = np.where((~has_skin_col) & (np.arange(W) >= xmin) & (np.arange(W) <= xmax))[0]
if len(valid_cols) > 2:
    top_center[xmin:xmax + 1] = np.interp(np.arange(xmin, xmax + 1), valid_cols, top_center[valid_cols])
    bot_center[xmin:xmax + 1] = np.interp(np.arange(xmin, xmax + 1), valid_cols, bot_center[valid_cols])

from scipy.ndimage import median_filter
med_k = max(25, (int(W * 0.07) // 2) * 2 + 1)
top_center[xmin:xmax + 1] = median_filter(top_center[xmin:xmax + 1], size=med_k, mode="nearest")
bot_center[xmin:xmax + 1] = median_filter(bot_center[xmin:xmax + 1], size=med_k, mode="nearest")

show_curve(img_clean, np.arange(xmin, xmax + 1), top_center[xmin:xmax + 1], color=(0, 255, 255),
           title="9. TOP (cyan) / BOTTOM (magenta) search-band centers — anchors, not final paths",
           extra=[(np.arange(xmin, xmax + 1), bot_center[xmin:xmax + 1], (255, 0, 255))])

# %%
# 10. TOP/BOTTOM cost fields the DP will minimize over (dark = cheap to pass through).
vert_cost_top = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable,
                                  orientation="vertical", edge_type="top",
                                  center_ref=top_center, skin_mask=skin_mask)
vert_cost_bot = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable,
                                  orientation="vertical", edge_type="bottom",
                                  center_ref=bot_center, skin_mask=skin_mask)
show_gray(vert_cost_top, "10. TOP cost field (dark = cheap)")
show_gray(vert_cost_bot, "10. BOTTOM cost field (dark = cheap)")

# %%
# 11. Traced TOP/BOTTOM curves (DP shortest path, multi-seed search).
band_r = max(30, int(H * 0.08))
top_path, top_cost, top_seed = trace_with_seed_search(
    vert_cost_top, "cols", top_center, band_r, xmin, xmax, w_jump=0.08, w_curv=0.08
)
bot_path, bot_cost, bot_seed = trace_with_seed_search(
    vert_cost_bot, "cols", bot_center, band_r, xmin, xmax, w_jump=0.08, w_curv=0.08
)
print(f"TOP cost={top_cost:.1f} seed_col={top_seed}")
print(f"BOTTOM cost={bot_cost:.1f} seed_col={bot_seed}")
show_curve(img_clean, np.arange(xmin, xmax + 1), top_path, color=(0, 0, 255),
           title="11. Traced TOP (red) / BOTTOM (blue)",
           extra=[(np.arange(xmin, xmax + 1), bot_path, (255, 0, 0))])

# %%
# 12. LEFT/RIGHT search-band centers, pinned at TOP's/BOTTOM's own endpoints.
top_left_row, top_right_row = int(top_path[0]), int(top_path[-1])
bot_left_row, bot_right_row = int(bot_path[0]), int(bot_path[-1])
left_row_lo, left_row_hi = sorted((top_left_row, bot_left_row))
right_row_lo, right_row_hi = sorted((top_right_row, bot_right_row))

left_center = np.full(H, xmin)
right_center = np.full(H, xmax)
for y in range(max(0, min(left_row_lo, right_row_lo) - 5), min(H, max(left_row_hi, right_row_hi) + 5)):
    rowmask = np.where(mask[y, :] > 0)[0]
    if len(rowmask) > 0:
        left_center[y] = rowmask.min()
        right_center[y] = rowmask.max()

show_curve(img_clean, left_center[left_row_lo:left_row_hi + 1], np.arange(left_row_lo, left_row_hi + 1),
           color=(0, 255, 255), title="12. LEFT (cyan) / RIGHT (magenta) search-band centers",
           extra=[(right_center[right_row_lo:right_row_hi + 1],
                   np.arange(right_row_lo, right_row_hi + 1), (255, 0, 255))])

# %%
# 13. LEFT/RIGHT cost fields.
horiz_cost_left = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable,
                                    orientation="horizontal", edge_type="left",
                                    center_ref=left_center, skin_mask=skin_mask)
horiz_cost_right = local_cost_field(mag_n, gx, gy, dist, nx, ny, mask_reliable,
                                     orientation="horizontal", edge_type="right",
                                     center_ref=right_center, skin_mask=skin_mask)
show_gray(horiz_cost_left, "13. LEFT cost field (dark = cheap)")
show_gray(horiz_cost_right, "13. RIGHT cost field (dark = cheap)")

# %%
# 14. Traced LEFT/RIGHT curves.
band_r_lr = max(20, int(W * 0.04))
left_path, left_cost = dp_trace_1d(
    horiz_cost_left, "rows", left_center, band_r_lr,
    index_range=(left_row_lo, left_row_hi), pin_start=xmin, pin_end=xmin,
)
right_path, right_cost = dp_trace_1d(
    horiz_cost_right, "rows", right_center, band_r_lr,
    index_range=(right_row_lo, right_row_hi), pin_start=xmax, pin_end=xmax,
)
print(f"LEFT cost={left_cost:.1f}  RIGHT cost={right_cost:.1f}")
show_curve(img_clean, left_path, np.arange(left_row_lo, left_row_hi + 1), color=(0, 200, 0),
           title="14. Traced LEFT (green) / RIGHT (orange)",
           extra=[(right_path, np.arange(right_row_lo, right_row_hi + 1), (0, 165, 255))])

# %%
# 15. Dense LEFT(y)/RIGHT(y) — full-height interpolation the GUTTER trace will
# be hard-bounded by.
left_dense = dense_interp(left_path, left_row_lo, left_row_hi, H) + 3
right_dense = dense_interp(right_path, right_row_lo, right_row_hi, H) - 3

plt.figure(figsize=(10, 4))
plt.plot(left_dense, label="left_dense")
plt.plot(right_dense, label="right_dense")
plt.legend()
plt.title("15. Dense LEFT(y)/RIGHT(y)")
plt.xlabel("row")
plt.ylabel("column")
plt.show()

# %%
# 16. Spine V-notch points — where TOP's and BOTTOM's curves bend sharpest,
# i.e. where the two pages actually meet.
top_v_idx, top_v_y = find_v_shaped_contact_point(top_path, edge_type="top")
bot_v_idx, bot_v_y = find_v_shaped_contact_point(bot_path, edge_type="bottom")
top_v_col = xmin + top_v_idx
bot_v_col = xmin + bot_v_idx
print(f"TOP V-notch:    ({top_v_col}, {top_v_y})")
print(f"BOTTOM V-notch: ({bot_v_col}, {bot_v_y})")

vis = img_clean.copy()
cv2.circle(vis, (top_v_col, top_v_y), 10, (255, 0, 255), -1)
cv2.circle(vis, (bot_v_col, bot_v_y), 10, (255, 0, 255), -1)
show(vis, "16. Detected spine V-notch points (magenta dots)")

# %%
# 17. GUTTER cost field (darkness + ridge/symmetry), then the final gutter —
# a straight line between the two V-notches, clipped to stay strictly inside
# LEFT(y)/RIGHT(y).
gutter_row_lo = max(0, min(top_v_y, H - 2))
gutter_row_hi = min(H - 1, max(bot_v_y, gutter_row_lo + 1))
gcost = gutter_local_cost(gray_clean, left_dense, right_dense)
show_gray(gcost, "17a. Gutter local cost field (dark = gutter-like)")

aspect_ratio = W / H
gutter_present = aspect_ratio > 1.15
print(f"aspect_ratio={aspect_ratio:.2f} gutter_present={gutter_present}")

rows_arr = np.arange(gutter_row_lo, gutter_row_hi + 1)
if gutter_present:
    if top_v_y != bot_v_y:
        slope = (bot_v_col - top_v_col) / float(bot_v_y - top_v_y)
        gutter_cols = top_v_col + slope * (rows_arr - top_v_y)
    else:
        gutter_cols = np.full(len(rows_arr), top_v_col)
    gutter_path = np.round(gutter_cols).astype(int)
    for i, y in enumerate(rows_arr):
        lo, hi = left_dense[y], right_dense[y]
        gutter_path[i] = int(np.clip(gutter_path[i], lo + 2, hi - 2))

    show_curve(img_clean, gutter_path, rows_arr, color=(255, 0, 255),
               title="17b. Final GUTTER (straight line between V-notches)")
else:
    print("No gutter — single page (aspect ratio doesn't look like a spread).")

# %%
# 18. FINAL: all 5 curves together — exactly what detect_five_curves() /
# draw_overlay_only() would produce for this image, plus the diagnostic
# contrast-consistency numbers detect_five_curves() prints.
curves = {
    "top": (np.arange(xmin, xmax + 1), top_path),
    "bottom": (np.arange(xmin, xmax + 1), bot_path),
    "left": (left_path, np.arange(left_row_lo, left_row_hi + 1)),
    "right": (right_path, np.arange(right_row_lo, right_row_hi + 1)),
}
if gutter_present:
    curves["gutter"] = (gutter_path, rows_arr)

final_vis = draw_overlay_only(img_clean, curves)
show(final_vis, "18. FINAL: all 5 curves")

top_pol, top_trans = contrast_consistency_score(gray_clean, top_path, "cols")
bot_pol, bot_trans = contrast_consistency_score(gray_clean, bot_path, "cols")
left_pol, left_trans = contrast_consistency_score(gray_clean, left_path, "rows")
right_pol, right_trans = contrast_consistency_score(gray_clean, right_path, "rows")
print("contrast-consistency (polarity 0-1, mean |transition|):")
print(f"  TOP    {top_pol:.2f} / {top_trans:.1f}")
print(f"  BOTTOM {bot_pol:.2f} / {bot_trans:.1f}")
print(f"  LEFT   {left_pol:.2f} / {left_trans:.1f}")
print(f"  RIGHT  {right_pol:.2f} / {right_trans:.1f}")
