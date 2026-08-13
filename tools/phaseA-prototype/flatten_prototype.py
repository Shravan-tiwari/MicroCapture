"""
Phase B prototype: point-to-rectangle flattening, driven by Phase A's 5
detected curves (TOP, BOTTOM, LEFT, RIGHT, GUTTER). Diagnostic-only, does not
touch the C# app. See /Users/shravantiwari/.claude/plans/fizzy-wondering-pascal.md
Phase B for the full spec.

Pipeline per spread image:
  1. Run Phase A's detect_five_curves() to get the 5 boundary curves.
  2. Split the whole-spread boundary at GUTTER into a LEFT-page and
     RIGHT-page point set (each: its own share of TOP/BOTTOM + its own
     outer edge + the shared GUTTER edge).
  3. Arc-length-resample each page's 4 boundary curves to a shared
     t in [0,1] parameter (not raw DP point index correspondence).
  4. Build a Coons-patch bilinear boundary map (same math as
     ImageProcessor.cs's RectifyWithBoundaryCurves, ImageProcessor.cs:2437)
     from the 4 arc-length-parameterized curves directly (skips the
     degree-2 poly-fit step already validated in the C# code, since Phase A
     hands over dense multi-point curves, not just 2 endpoints).
  5. cv2.remap each page with mapX/mapY built directly from the Coons patch
     (S(u,v) evaluated once per destination pixel — no inversion needed
     since destination is sampled uniformly in (u,v)).
  6. Run the numeric validation checks from Phase B step 5/7 and report
     PASS/FLAG per check, per page.
"""

import os
import sys
import csv
import numpy as np
import cv2

sys.path.insert(0, os.path.dirname(__file__))
from boundary_prototype import (
    detect_five_curves, load_and_prepare, list_all_fixtures,
    PRIMARY_DIFFICULT_IMAGE,
)

OUT_ROOT = os.path.join(os.path.dirname(__file__), "phaseB_results")

# ----------------------------------------------------------------------------
# Step 2/3: split whole-spread boundary at GUTTER, arc-length resample
# ----------------------------------------------------------------------------

def dense_clamped(path, idx_lo, idx_hi, full_len):
    xs = np.arange(idx_lo, idx_hi + 1)
    return np.interp(np.arange(full_len), xs, path, left=path[0], right=path[-1])


def arc_length_resample(pts, n=120):
    """pts: Nx2 array (x,y) in traversal order. Returns n points evenly
    spaced by cumulative arc length, param t in [0,1]."""
    pts = np.asarray(pts, dtype=np.float64)
    d = np.sqrt(np.sum(np.diff(pts, axis=0) ** 2, axis=1))
    cum = np.concatenate([[0], np.cumsum(d)])
    total = cum[-1]
    if total < 1e-6:
        return np.repeat(pts[:1], n, axis=0)
    target = np.linspace(0, total, n)
    x = np.interp(target, cum, pts[:, 0])
    y = np.interp(target, cum, pts[:, 1])
    return np.stack([x, y], axis=1)


def split_pages(img_shape, curves, diagnostics):
    """Returns dict: {'left': {'top':pts,'bottom':pts,'outer':pts,'gutter':pts},
                       'right': {...}} in image (x,y) order, each still raw
    (not yet arc-length resampled)."""
    H, W = img_shape[:2]
    top_xs, top_ys = curves["top"]
    bot_xs, bot_ys = curves["bottom"]
    left_xs, left_ys = curves["left"]      # (x per row, row)
    right_xs, right_ys = curves["right"]

    if "gutter" not in curves:
        return None  # single page, nothing to split

    gutter_xs, gutter_ys = curves["gutter"]
    g_row_lo, g_row_hi = int(gutter_ys.min()), int(gutter_ys.max())
    gutter_dense = dense_clamped(gutter_xs, g_row_lo, g_row_hi, H)

    # split TOP curve: for each column, compare its own y (row) against
    # gutter's x at that same row -> left-of-gutter vs right-of-gutter.
    def split_curve_by_column(xs, ys):
        gx_at_row = gutter_dense[np.clip(ys.astype(int), 0, H - 1)]
        is_left = xs < gx_at_row
        # find the crossing index (last True before first False), so both
        # sub-curves share the crossing point (no 1-pixel gap in the split)
        if is_left.all():
            cross = len(xs) - 1
        elif not is_left.any():
            cross = 0
        else:
            cross = int(np.argmax(~is_left))  # first index NOT left
        left_pts = np.stack([xs[: cross + 1], ys[: cross + 1]], axis=1)
        right_pts = np.stack([xs[cross:], ys[cross:]], axis=1)
        return left_pts, right_pts

    top_left, top_right = split_curve_by_column(top_xs, top_ys)
    bot_left, bot_right = split_curve_by_column(bot_xs, bot_ys)

    left_outer = np.stack([left_xs, left_ys], axis=1)
    right_outer = np.stack([right_xs, right_ys], axis=1)

    # gutter edge sampled over each page's own outer-curve row range (the
    # outer curves are pinned corner-to-corner by Phase A's construction, so
    # their row range IS the page's true full height).
    l_row_lo, l_row_hi = int(left_ys.min()), int(left_ys.max())
    r_row_lo, r_row_hi = int(right_ys.min()), int(right_ys.max())
    left_gutter_rows = np.arange(l_row_lo, l_row_hi + 1)
    right_gutter_rows = np.arange(r_row_lo, r_row_hi + 1)
    left_gutter_pts = np.stack([gutter_dense[left_gutter_rows], left_gutter_rows], axis=1)
    right_gutter_pts = np.stack([gutter_dense[right_gutter_rows], right_gutter_rows], axis=1)

    return {
        "left": {"top": top_left, "bottom": bot_left, "outer": left_outer, "gutter": left_gutter_pts},
        "right": {"top": top_right, "bottom": bot_right, "outer": right_outer, "gutter": right_gutter_pts},
    }


# ----------------------------------------------------------------------------
# Step 4/5: Coons patch boundary map + remap
# ----------------------------------------------------------------------------

def coons_map(top_pts, bottom_pts, left_pts, right_pts, w_out, h_out, n_resample=120):
    """top/bottom: curves parameterized left(u=0)->right(u=1).
    left/right: curves parameterized top(v=0)->bottom(v=1).
    Returns mapX, mapY of shape (h_out, w_out) — source (x,y) per dest pixel.
    """
    top_r = arc_length_resample(top_pts, n_resample)
    bot_r = arc_length_resample(bottom_pts, n_resample)
    left_r = arc_length_resample(left_pts, n_resample)
    right_r = arc_length_resample(right_pts, n_resample)

    t_param = np.linspace(0, 1, n_resample)
    u = np.linspace(0, 1, w_out)
    v = np.linspace(0, 1, h_out)

    def interp_curve(curve, param, new_param):
        x = np.interp(new_param, param, curve[:, 0])
        y = np.interp(new_param, param, curve[:, 1])
        return x, y

    Top_x, Top_y = interp_curve(top_r, t_param, u)       # len w_out
    Bot_x, Bot_y = interp_curve(bot_r, t_param, u)        # len w_out
    Left_x, Left_y = interp_curve(left_r, t_param, v)     # len h_out
    Right_x, Right_y = interp_curve(right_r, t_param, v)  # len h_out

    U, V = np.meshgrid(u, v)  # (h_out, w_out)

    mapX = ((1 - V) * Top_x[np.newaxis, :] + V * Bot_x[np.newaxis, :]
            + (1 - U) * Left_x[:, np.newaxis] + U * Right_x[:, np.newaxis]
            - ((1 - U) * (1 - V) * Top_x[0] + U * (1 - V) * Top_x[-1]
               + (1 - U) * V * Bot_x[0] + U * V * Bot_x[-1]))
    mapY = ((1 - V) * Top_y[np.newaxis, :] + V * Bot_y[np.newaxis, :]
            + (1 - U) * Left_y[:, np.newaxis] + U * Right_y[:, np.newaxis]
            - ((1 - U) * (1 - V) * Top_y[0] + U * (1 - V) * Top_y[-1]
               + (1 - U) * V * Bot_y[0] + U * V * Bot_y[-1]))

    return mapX.astype(np.float32), mapY.astype(np.float32), {
        "top": top_r, "bottom": bot_r, "left": left_r, "right": right_r,
    }


def flatten_page(img, page_curves, side):
    top_pts = page_curves["top"]
    bot_pts = page_curves["bottom"]
    if side == "left":
        # u=0 -> outer(left) edge, u=1 -> gutter edge
        left_edge = page_curves["outer"]
        right_edge = page_curves["gutter"]
        # top/bottom must be ordered u=0(outer)->u=1(gutter): outer page's
        # top/bottom split already runs left(low x)->right(high x, at gutter)
    else:
        # u=0 -> gutter edge, u=1 -> outer(right) edge
        left_edge = page_curves["gutter"]
        right_edge = page_curves["outer"]

    # left/right edges must run v=0(top)->v=1(bottom); outer/gutter arrays
    # are already row-ascending by construction.
    top_len = np.sum(np.sqrt(np.sum(np.diff(top_pts, axis=0) ** 2, axis=1)))
    bot_len = np.sum(np.sqrt(np.sum(np.diff(bot_pts, axis=0) ** 2, axis=1)))
    left_len = np.sum(np.sqrt(np.sum(np.diff(left_edge, axis=0) ** 2, axis=1)))
    right_len = np.sum(np.sqrt(np.sum(np.diff(right_edge, axis=0) ** 2, axis=1)))

    w_out = max(50, int(round((top_len + bot_len) / 2)))
    h_out = max(50, int(round((left_len + right_len) / 2)))
    # cap to keep remap cheap/sane for a prototype
    w_out = min(w_out, 2000)
    h_out = min(h_out, 2600)

    mapX, mapY, resampled = coons_map(top_pts, bot_pts, left_edge, right_edge, w_out, h_out)
    flat = cv2.remap(img, mapX, mapY, interpolation=cv2.INTER_LINEAR,
                      borderMode=cv2.BORDER_REPLICATE)
    return flat, mapX, mapY, resampled, (w_out, h_out)


# ----------------------------------------------------------------------------
# Step 5/7: numeric validation checks
# ----------------------------------------------------------------------------

def validate_page(flat, mapX, mapY, resampled, size):
    w_out, h_out = size
    checks = {}

    # (a) boundary reprojection error: by construction the Coons map is
    # EXACT on the boundary (u=0/1, v=0/1 edges reproduce the input curves
    # exactly) — verify this numerically rather than just asserting it.
    top_r, bot_r, left_r, right_r = resampled["top"], resampled["bottom"], resampled["left"], resampled["right"]
    u = np.linspace(0, 1, mapX.shape[1])
    err_top = np.max(np.abs(mapX[0, :] - np.interp(u, np.linspace(0, 1, len(top_r)), top_r[:, 0])))
    checks["boundary_reprojection_max_px"] = float(err_top)
    checks["boundary_reprojection_pass"] = err_top < 1.0  # sub-pixel by construction

    # (b) aspect ratio plausibility (single printed page ~0.6-0.9 portrait typically;
    # allow a broad band since fixtures vary)
    aspect = w_out / h_out
    checks["aspect_ratio"] = float(aspect)
    checks["aspect_ratio_pass"] = 0.4 <= aspect <= 1.3

    # (c) corner angle deviation from 90 degrees, all 4 corners
    def angle_at(p_center, p_a, p_b):
        v1 = p_a - p_center
        v2 = p_b - p_center
        cos = np.dot(v1, v2) / (np.linalg.norm(v1) * np.linalg.norm(v2) + 1e-9)
        return np.degrees(np.arccos(np.clip(cos, -1, 1)))

    TL, TR = np.array([mapX[0, 0], mapY[0, 0]]), np.array([mapX[0, -1], mapY[0, -1]])
    BL, BR = np.array([mapX[-1, 0], mapY[-1, 0]]), np.array([mapX[-1, -1], mapY[-1, -1]])
    angles = [
        angle_at(TL, TR, BL), angle_at(TR, TL, BR),
        angle_at(BL, TL, BR), angle_at(BR, TR, BL),
    ]
    max_dev = max(abs(a - 90) for a in angles)
    checks["corner_angle_max_deviation_deg"] = float(max_dev)
    checks["corner_angle_pass"] = max_dev < 15.0

    # (d) gutter-edge straightness post-remap: the gutter side of the output
    # (column 0 for right-page, column w_out-1 for left-page — whichever
    # side corresponds to the gutter edge) should come out close to vertical.
    # We check both edges generically here (whichever is straighter is the
    # informative one; caller knows which side is gutter).
    left_col_x = mapX[:, 0]
    right_col_x = mapX[:, -1]
    left_dev = float(np.max(left_col_x) - np.min(left_col_x))
    right_dev = float(np.max(right_col_x) - np.min(right_col_x))
    checks["left_edge_x_spread_px"] = left_dev
    checks["right_edge_x_spread_px"] = right_dev

    # (e) local scale/stretch outlier check: sample local Jacobian magnitude
    # across the map, flag extreme outliers relative to the map's own median.
    gy, gx = np.gradient(mapY), np.gradient(mapX)
    # gx/gy each returns (d/drow, d/dcol) tuples
    dXdrow, dXdcol = gx
    dYdrow, dYdcol = gy
    scale = np.sqrt(dXdcol ** 2 + dYdcol ** 2) * np.sqrt(dXdrow ** 2 + dYdrow ** 2)
    med = np.median(scale)
    ratio = scale / (med + 1e-6)
    outlier_frac = float(np.mean((ratio > 4) | (ratio < 0.25)))
    checks["stretch_outlier_fraction"] = outlier_frac
    checks["stretch_outlier_pass"] = outlier_frac < 0.05

    return checks


# ----------------------------------------------------------------------------
# Batch driver
# ----------------------------------------------------------------------------

# Representative subset per plan step 6: cover book-curve, trapezoid,
# visible page-stack, difficult gutter, diagrams (curve.jpeg has concentric
# circles), dense text. Picked by inspecting Phase A's CSV + fixture names.
REPRESENTATIVE_SUBSET = [
    "IMG_0022.JPG",             # primary difficult case (page-stack visible), PASS in Phase A
    "IMG_0021.JPG",             # book-curve, FLAG in Phase A (top edge)
    "IMG_0027.JPG",             # book-curve, PASS
    "IMG_0028.JPG",             # book-curve, PASS
    "Trapezoid_Image001.JPG",   # trapezoid/perspective, FLAG (top edge)
    "Trapezoid_Image002.JPG",   # trapezoid, FLAG (top+bottom), V-cradle per memory
    "Trapezoid_Image003.JPG",   # single page steep angle, no gutter -> exercises the "no split" path
    "Trapezoid_Image004.JPG",   # single page, clean control case
    "curve.jpeg",                # diagrams/circles, new user-supplied image
]


def run_subset():
    out_dir = os.path.join(OUT_ROOT, "current")
    os.makedirs(out_dir, exist_ok=True)
    all_paths = {os.path.basename(p): p for p in list_all_fixtures()}

    csv_rows = []
    for name in REPRESENTATIVE_SUBSET:
        path = all_paths.get(name)
        if path is None:
            print(f"SKIP {name}: not found in fixture set")
            continue
        print(f"\n=== {name} ===")
        img, curves, diag = detect_five_curves(path, verbose=False)
        pages = split_pages(img.shape, curves, diag)
        base = os.path.splitext(name)[0]

        # save the original (resized-for-processing) image for side-by-side
        cv2.imwrite(os.path.join(out_dir, f"{base}_00_source.png"), img)

        if pages is None:
            # no gutter -> whole-image-as-one-page flattening using top/bottom/left/right directly
            flat, mapX, mapY, resampled, size = flatten_page(
                img,
                {"top": np.stack(curves["top"], axis=1), "bottom": np.stack(curves["bottom"], axis=1),
                 "outer": np.stack(curves["left"], axis=1), "gutter": np.stack(curves["right"], axis=1)},
                "left",
            )
            checks = validate_page(flat, mapX, mapY, resampled, size)
            cv2.imwrite(os.path.join(out_dir, f"{base}_single_flat.png"), flat)
            row = {"image": name, "page": "single"}
            row.update({k: v for k, v in checks.items()})
            csv_rows.append(row)
            print(f"  single-page flatten -> {size}, checks: "
                  f"{ {k: v for k, v in checks.items() if k.endswith('_pass')} }")
            continue

        for side in ("left", "right"):
            flat, mapX, mapY, resampled, size = flatten_page(img, pages[side], side)
            checks = validate_page(flat, mapX, mapY, resampled, size)
            cv2.imwrite(os.path.join(out_dir, f"{base}_{side}_flat.png"), flat)
            row = {"image": name, "page": side}
            row.update({k: v for k, v in checks.items()})
            csv_rows.append(row)
            print(f"  {side} page flatten -> {size}, checks: "
                  f"{ {k: v for k, v in checks.items() if k.endswith('_pass')} }")

    if csv_rows:
        csv_path = os.path.join(out_dir, "phaseB_results.csv")
        fieldnames = list(csv_rows[0].keys())
        for r in csv_rows:
            for k in fieldnames:
                r.setdefault(k, "")
        with open(csv_path, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=fieldnames)
            w.writeheader()
            w.writerows(csv_rows)
        print(f"\nWrote {csv_path}")

    build_contact_sheet(out_dir)


def build_contact_sheet(out_dir, thumb_w=340):
    import glob
    files = sorted(glob.glob(os.path.join(out_dir, "*_flat.png")))
    src_files = sorted(glob.glob(os.path.join(out_dir, "*_00_source.png")))
    entries = []
    for f in src_files:
        img = cv2.imread(f)
        label = os.path.basename(f).replace("_00_source.png", " (source)")
        entries.append((label, img))
    for f in files:
        img = cv2.imread(f)
        label = os.path.basename(f).replace(".png", "")
        entries.append((label, img))

    cols = 4
    n = len(entries)
    rows = (n + cols - 1) // cols
    label_h = 30
    thumbs = []
    for label, img in entries:
        h, w = img.shape[:2]
        scale = thumb_w / w
        thumbs.append((label, cv2.resize(img, (thumb_w, int(h * scale)))))
    row_heights = []
    for r in range(rows):
        row_thumbs = thumbs[r * cols:(r + 1) * cols]
        row_heights.append(max(t.shape[0] for _, t in row_thumbs) if row_thumbs else 0)
    row_y0 = [0]
    for r in range(rows):
        row_y0.append(row_y0[-1] + row_heights[r] + label_h)
    canvas = np.full((row_y0[-1], thumb_w * cols, 3), 255, dtype=np.uint8)
    for i, (label, thumb) in enumerate(thumbs):
        r, c = divmod(i, cols)
        y0, x0 = row_y0[r], c * thumb_w
        canvas[y0:y0 + thumb.shape[0], x0:x0 + thumb.shape[1]] = thumb
        cv2.putText(canvas, label, (x0 + 4, y0 + row_heights[r] + 22), cv2.FONT_HERSHEY_SIMPLEX,
                    0.4, (0, 0, 0), 1, cv2.LINE_AA)
        cv2.rectangle(canvas, (x0, y0), (x0 + thumb_w - 1, y0 + row_heights[r] + label_h - 1), (200, 200, 200), 1)
    sheet_path = os.path.join(out_dir, "phaseB_contact_sheet.png")
    cv2.imwrite(sheet_path, canvas)
    print(f"Wrote {sheet_path}")


if __name__ == "__main__":
    run_subset()
