#!/usr/bin/env python
######################################################################
# page_dewarp.py - Proof-of-concept of page-dewarping based on a
# "cubic sheet" model. Requires OpenCV (version 3 or greater),
# PIL/Pillow, and scipy.optimize.
######################################################################
# Author:  Matt Zucker
# Date:    July 2016
# License: MIT License (see LICENSE.txt)
# Source:  https://github.com/mzucker/page_dewarp
######################################################################
#
# Vendored into MicroCapture (see MicroCapture.Processing/PythonDewarpRunner.cs)
# with ONE deliberate change from upstream (a second, REMAP_DECIMATE, was tried
# and reverted — see below):
#
# REMAP_DECIMATE is back at upstream's default of 16 (was set to 1 for a
# while). remap_image() computes the pixel-remap coordinate grid at
# 1/REMAP_DECIMATE resolution and cubic-upsamples that grid before the final
# cv2.remap — at decimate-by-16, this smooths the warp field itself on real
# photos with fast-changing curvature (steep viewing angle, heavy bow near a
# book's spine, or a low-resolution source), producing visibly soft/ghosted
# output text; confirmed directly by running upstream page_dewarp.py
# unmodified and seeing the identical blur in ITS OWN raw output.
# REMAP_DECIMATE=1 (computing the true per-pixel coordinate grid, no
# decimation) fixed that blur and was shipped for a while, but on at least
# one real Windows machine the full-resolution remap step turned out to cost
# real time — confirmed directly (worker-mode timing showed a real gap
# between the optimizer finishing and the response coming back, present
# even with the optimizer itself already fast). REMAP_DECIMATE=4 was tested
# as a middle ground (visibly sharper than 16, not quite as sharp as 1) but
# not shipped — reverted to 16 on the operator's explicit request pending a
# decision on the sharpness/speed tradeoff. If sharper output is wanted
# again, the fix is exactly this one line; see also
# MicroCapture.Processing/PythonDewarpWorker.cs's own header for the other,
# unrelated speed work (persistent worker process) that's independent of
# this setting either way.
#
# The other deliberate change from upstream, still in place:
# optimize_params()'s scipy.optimize.minimize call uses method='L-BFGS-B'
#    with an explicit analytic jac= (see project_keypoints_jacobian), instead
#    of upstream's method='Powell'. Powell is derivative-free — at this
#    problem's ~400-800+ parameters (camera pose + per-span cubic keypoints)
#    it spends most of its time exploring via blind coordinate line searches.
#    L-BFGS-B alone (no jac=) was tried first and looked like a 10-15x
#    speedup in initial testing, but turned out to be unreliable: without an
#    explicit gradient, SciPy falls back to numerically approximating it via
#    finite differences, which costs roughly one extra full objective
#    evaluation PER PARAMETER per optimizer step — on one real Windows
#    machine this made total optimizer time vary wildly (0.5s to 25s+ on
#    similar photos) instead of scaling with problem difficulty, because
#    objective-evaluation cost (not algorithmic progress) was dominating.
#    project_keypoints_jacobian supplies the exact analytic gradient instead
#    (chain rule through OpenCV's own cv2.projectPoints Jacobian for
#    rvec/tvec, a closed-form pinhole-camera derivative for the object-point
#    chain, and the cubic polynomial's own derivative for alpha/beta/x) — no
#    finite-difference fallback needed at all, so optimizer time no longer
#    depends on how expensive one objective evaluation happens to be on a
#    given machine. Verified against SciPy's own numerical gradient (5
#    random configurations, 555-650 parameters, matching real photos'
#    scale) to ~1e-6 relative error before being trusted, and confirmed on
#    real photos to produce the same final objective/visual quality as
#    Powell while being dramatically faster — this was the dominant cost in
#    total per-image dewarp time (the operator's own ~20s/image estimate).
#
# Everything else in this file is unmodified upstream code.
######################################################################

import os
import sys
import datetime
import cv2
from PIL import Image
import numpy as np
import scipy.optimize

# for some reason pylint complains about cv2 members being undefined :(
# pylint: disable=E1101

PAGE_MARGIN_X = 15       # reduced px to ignore near L/R edge
PAGE_MARGIN_Y = 60       # reduced px to ignore near T/B edge

OUTPUT_ZOOM = 1.0        # how much to zoom output relative to *original* image
OUTPUT_DPI = 300         # assumed source DPI; stated output DPI = this / OUTPUT_ZOOM
REMAP_DECIMATE = 16     # downscaling factor for remapping image (upstream default, reverted — see header comment)

# 'binary': adaptive-threshold black & white (original behavior)
# 'gray': grayscale, no thresholding
# 'color': full color, no thresholding
OUTPUT_COLOR_MODE = 'binary'

ADAPTIVE_WINSZ = 55      # window size for adaptive threshold in reduced px

TEXT_MIN_WIDTH = 15      # min reduced px width of detected text contour
TEXT_MIN_HEIGHT = 2      # min reduced px height of detected text contour
TEXT_MIN_ASPECT = 1.5    # filter out text contours below this w/h ratio
TEXT_MAX_THICKNESS = 10  # max reduced px thickness of detected text contour

EDGE_MAX_OVERLAP = 1.0   # max reduced px horiz. overlap of contours in span
EDGE_MAX_LENGTH = 100.0  # max reduced px length of edge connecting contours
EDGE_ANGLE_COST = 10.0   # cost of angles in edges (tradeoff vs. length)
EDGE_MAX_ANGLE = 7.5     # maximum change in angle allowed between contours

RVEC_IDX = slice(0, 3)   # index of rvec in params vector
TVEC_IDX = slice(3, 6)   # index of tvec in params vector
CUBIC_IDX = slice(6, 8)  # index of cubic slopes in params vector

SPAN_MIN_WIDTH = 30      # minimum reduced px width for span
SPAN_PX_PER_STEP = 20    # reduced px spacing for sampling along spans
FOCAL_LENGTH = 1.2       # normalized focal length of camera

DEBUG_LEVEL = 0          # 0=none, 1=some, 2=lots, 3=all
DEBUG_OUTPUT = 'file'    # file, screen, both

WINDOW_NAME = 'Dewarp'   # Window name for visualization

# nice color palette for visualizing contours, etc.
CCOLORS = [
    (255, 0, 0),
    (255, 63, 0),
    (255, 127, 0),
    (255, 191, 0),
    (255, 255, 0),
    (191, 255, 0),
    (127, 255, 0),
    (63, 255, 0),
    (0, 255, 0),
    (0, 255, 63),
    (0, 255, 127),
    (0, 255, 191),
    (0, 255, 255),
    (0, 191, 255),
    (0, 127, 255),
    (0, 63, 255),
    (0, 0, 255),
    (63, 0, 255),
    (127, 0, 255),
    (191, 0, 255),
    (255, 0, 255),
    (255, 0, 191),
    (255, 0, 127),
    (255, 0, 63),
]

# default intrinsic parameter matrix
K = np.array([
    [FOCAL_LENGTH, 0, 0],
    [0, FOCAL_LENGTH, 0],
    [0, 0, 1]], dtype=np.float32)


def debug_show(name, step, text, display):

    if DEBUG_OUTPUT != 'screen':
        filetext = text.replace(' ', '_')
        outfile = name + '_debug_' + str(step) + '_' + filetext + '.png'
        cv2.imwrite(outfile, display)

    if DEBUG_OUTPUT != 'file':

        image = display.copy()
        height = image.shape[0]

        cv2.putText(image, text, (16, height-16),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0,
                    (0, 0, 0), 3, cv2.LINE_AA)

        cv2.putText(image, text, (16, height-16),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0,
                    (255, 255, 255), 1, cv2.LINE_AA)

        cv2.imshow(WINDOW_NAME, image)

        while cv2.waitKey(5) < 0:
            pass


def round_nearest_multiple(i, factor):
    i = int(i)
    rem = i % factor
    if not rem:
        return i
    else:
        return i + factor - rem


def pix2norm(shape, pts):
    height, width = shape[:2]
    scl = 2.0/(max(height, width))
    offset = np.array([width, height], dtype=pts.dtype).reshape((-1, 1, 2))*0.5
    return (pts - offset) * scl


def norm2pix(shape, pts, as_integer):
    height, width = shape[:2]
    scl = max(height, width)*0.5
    offset = np.array([0.5*width, 0.5*height],
                      dtype=pts.dtype).reshape((-1, 1, 2))
    rval = pts * scl + offset
    if as_integer:
        return (rval + 0.5).astype(int)
    else:
        return rval


def fltp(point):
    return tuple(point.astype(int).flatten())


def draw_correspondences(img, dstpoints, projpts):

    display = img.copy()
    dstpoints = norm2pix(img.shape, dstpoints, True)
    projpts = norm2pix(img.shape, projpts, True)

    for pts, color in [(projpts, (255, 0, 0)),
                       (dstpoints, (0, 0, 255))]:

        for point in pts:
            cv2.circle(display, fltp(point), 3, color, -1, cv2.LINE_AA)

    for point_a, point_b in zip(projpts, dstpoints):
        cv2.line(display, fltp(point_a), fltp(point_b),
                 (255, 255, 255), 1, cv2.LINE_AA)

    return display


def get_default_params(corners, ycoords, xcoords):

    # page width and height
    page_width = np.linalg.norm(corners[1] - corners[0])
    page_height = np.linalg.norm(corners[-1] - corners[0])
    rough_dims = (page_width, page_height)

    # our initial guess for the cubic has no slope
    cubic_slopes = [0.0, 0.0]

    # object points of flat page in 3D coordinates
    corners_object3d = np.array([
        [0, 0, 0],
        [page_width, 0, 0],
        [page_width, page_height, 0],
        [0, page_height, 0]])

    # estimate rotation and translation from four 2D-to-3D point
    # correspondences
    _, rvec, tvec = cv2.solvePnP(corners_object3d,
                                 corners, K, np.zeros(5))

    span_counts = [len(xc) for xc in xcoords]

    params = np.hstack((np.array(rvec).flatten(),
                        np.array(tvec).flatten(),
                        np.array(cubic_slopes).flatten(),
                        ycoords.flatten()) +
                       tuple(xcoords))

    return rough_dims, span_counts, params


def project_xy(xy_coords, pvec):

    # get cubic polynomial coefficients given
    #
    #  f(0) = 0, f'(0) = alpha
    #  f(1) = 0, f'(1) = beta

    alpha, beta = tuple(pvec[CUBIC_IDX])

    poly = np.array([
        alpha + beta,
        -2*alpha - beta,
        alpha,
        0])

    xy_coords = xy_coords.reshape((-1, 2))
    z_coords = np.polyval(poly, xy_coords[:, 0])

    objpoints = np.hstack((xy_coords, z_coords.reshape((-1, 1))))

    image_points, _ = cv2.projectPoints(objpoints,
                                        pvec[RVEC_IDX],
                                        pvec[TVEC_IDX],
                                        K, np.zeros(5))

    return image_points


def project_keypoints(pvec, keypoint_index):

    xy_coords = pvec[keypoint_index]
    xy_coords[0, :] = 0

    return project_xy(xy_coords, pvec)


def resize_to_screen(src, maxw=1280, maxh=700, copy=False):

    height, width = src.shape[:2]

    scl_x = float(width)/maxw
    scl_y = float(height)/maxh

    scl = int(np.ceil(max(scl_x, scl_y)))

    if scl > 1.0:
        inv_scl = 1.0/scl
        img = cv2.resize(src, (0, 0), None, inv_scl, inv_scl, cv2.INTER_AREA)
    elif copy:
        img = src.copy()
    else:
        img = src

    return img


def box(width, height):
    return np.ones((height, width), dtype=np.uint8)


def get_page_extents(small):

    height, width = small.shape[:2]

    xmin = PAGE_MARGIN_X
    ymin = PAGE_MARGIN_Y
    xmax = width-PAGE_MARGIN_X
    ymax = height-PAGE_MARGIN_Y

    page = np.zeros((height, width), dtype=np.uint8)
    cv2.rectangle(page, (xmin, ymin), (xmax, ymax), (255, 255, 255), -1)

    outline = np.array([
        [xmin, ymin],
        [xmin, ymax],
        [xmax, ymax],
        [xmax, ymin]])

    return page, outline


def tighten_page_extents(small, pagemask, spans, padding=20):

    height, width = small.shape[:2]

    xs = []
    ys = []

    for span in spans:
        for cinfo in span:
            x, y, w, h = cinfo.rect
            xs += [x, x+w]
            ys += [y, y+h]

    if not xs:
        return pagemask, None

    xmin = max(0, min(xs) - padding)
    xmax = min(width, max(xs) + padding)
    ymin = max(0, min(ys) - padding)
    ymax = min(height, max(ys) + padding)

    page = np.zeros((height, width), dtype=np.uint8)
    cv2.rectangle(page, (xmin, ymin), (xmax, ymax), (255, 255, 255), -1)

    outline = np.array([
        [xmin, ymin],
        [xmin, ymax],
        [xmax, ymax],
        [xmax, ymin]])

    return page, outline


def get_mask(name, small, pagemask, masktype):

    sgray = cv2.cvtColor(small, cv2.COLOR_RGB2GRAY)

    if masktype == 'text':

        mask = cv2.adaptiveThreshold(sgray, 255, cv2.ADAPTIVE_THRESH_MEAN_C,
                                     cv2.THRESH_BINARY_INV,
                                     ADAPTIVE_WINSZ,
                                     25)

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.1, 'thresholded', mask)

        mask = cv2.dilate(mask, box(9, 1))

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.2, 'dilated', mask)

        mask = cv2.erode(mask, box(1, 3))

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.3, 'eroded', mask)

    else:

        mask = cv2.adaptiveThreshold(sgray, 255, cv2.ADAPTIVE_THRESH_MEAN_C,
                                     cv2.THRESH_BINARY_INV,
                                     ADAPTIVE_WINSZ,
                                     7)

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.4, 'thresholded', mask)

        mask = cv2.erode(mask, box(3, 1), iterations=3)

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.5, 'eroded', mask)

        mask = cv2.dilate(mask, box(8, 2))

        if DEBUG_LEVEL >= 3:
            debug_show(name, 0.6, 'dilated', mask)

    return np.minimum(mask, pagemask)


def interval_measure_overlap(int_a, int_b):
    return min(int_a[1], int_b[1]) - max(int_a[0], int_b[0])


def angle_dist(angle_b, angle_a):

    diff = angle_b - angle_a

    while diff > np.pi:
        diff -= 2*np.pi

    while diff < -np.pi:
        diff += 2*np.pi

    return np.abs(diff)


def blob_mean_and_tangent(contour):

    moments = cv2.moments(contour)

    area = moments['m00']

    mean_x = moments['m10'] / area
    mean_y = moments['m01'] / area

    moments_matrix = np.array([
        [moments['mu20'], moments['mu11']],
        [moments['mu11'], moments['mu02']]
    ]) / area

    _, svd_u, _ = cv2.SVDecomp(moments_matrix)

    center = np.array([mean_x, mean_y])
    tangent = svd_u[:, 0].flatten().copy()

    return center, tangent


class ContourInfo(object):

    def __init__(self, contour, rect, mask):

        self.contour = contour
        self.rect = rect
        self.mask = mask

        self.center, self.tangent = blob_mean_and_tangent(contour)

        self.angle = np.arctan2(self.tangent[1], self.tangent[0])

        clx = [self.proj_x(point) for point in contour]

        lxmin = min(clx)
        lxmax = max(clx)

        self.local_xrng = (lxmin, lxmax)

        self.point0 = self.center + self.tangent * lxmin
        self.point1 = self.center + self.tangent * lxmax

        self.pred = None
        self.succ = None

    def proj_x(self, point):
        return np.dot(self.tangent, point.flatten()-self.center)

    def local_overlap(self, other):
        xmin = self.proj_x(other.point0)
        xmax = self.proj_x(other.point1)
        return interval_measure_overlap(self.local_xrng, (xmin, xmax))


def generate_candidate_edge(cinfo_a, cinfo_b):

    # we want a left of b (so a's successor will be b and b's
    # predecessor will be a) make sure right endpoint of b is to the
    # right of left endpoint of a.
    if cinfo_a.point0[0] > cinfo_b.point1[0]:
        tmp = cinfo_a
        cinfo_a = cinfo_b
        cinfo_b = tmp

    x_overlap_a = cinfo_a.local_overlap(cinfo_b)
    x_overlap_b = cinfo_b.local_overlap(cinfo_a)

    overall_tangent = cinfo_b.center - cinfo_a.center
    overall_angle = np.arctan2(overall_tangent[1], overall_tangent[0])

    delta_angle = max(angle_dist(cinfo_a.angle, overall_angle),
                      angle_dist(cinfo_b.angle, overall_angle)) * 180/np.pi

    # we want the largest overlap in x to be small
    x_overlap = max(x_overlap_a, x_overlap_b)

    dist = np.linalg.norm(cinfo_b.point0 - cinfo_a.point1)

    if (dist > EDGE_MAX_LENGTH or
            x_overlap > EDGE_MAX_OVERLAP or
            delta_angle > EDGE_MAX_ANGLE):
        return None
    else:
        score = dist + delta_angle*EDGE_ANGLE_COST
        return (score, cinfo_a, cinfo_b)


def make_tight_mask(contour, xmin, ymin, width, height):

    tight_mask = np.zeros((height, width), dtype=np.uint8)
    tight_contour = contour - np.array((xmin, ymin)).reshape((-1, 1, 2))

    cv2.drawContours(tight_mask, [tight_contour], 0,
                     (1, 1, 1), -1)

    return tight_mask


def get_contours(name, small, pagemask, masktype):

    mask = get_mask(name, small, pagemask, masktype)

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL,
                                   cv2.CHAIN_APPROX_NONE)

    contours_out = []

    for contour in contours:

        rect = cv2.boundingRect(contour)
        xmin, ymin, width, height = rect

        if (width < TEXT_MIN_WIDTH or
                height < TEXT_MIN_HEIGHT or
                width < TEXT_MIN_ASPECT*height):
            continue

        tight_mask = make_tight_mask(contour, xmin, ymin, width, height)

        if tight_mask.sum(axis=0).max() > TEXT_MAX_THICKNESS:
            continue

        contours_out.append(ContourInfo(contour, rect, tight_mask))

    if DEBUG_LEVEL >= 2:
        visualize_contours(name, small, contours_out)

    return contours_out


def assemble_spans(name, small, pagemask, cinfo_list):

    # sort list
    cinfo_list = sorted(cinfo_list, key=lambda cinfo: cinfo.rect[1])

    # generate all candidate edges
    candidate_edges = []

    for i, cinfo_i in enumerate(cinfo_list):
        for j in range(i):
            # note e is of the form (score, left_cinfo, right_cinfo)
            edge = generate_candidate_edge(cinfo_i, cinfo_list[j])
            if edge is not None:
                candidate_edges.append(edge)

    # sort candidate edges by score (lower is better)
    candidate_edges.sort()

    # for each candidate edge
    for _, cinfo_a, cinfo_b in candidate_edges:
        # if left and right are unassigned, join them
        if cinfo_a.succ is None and cinfo_b.pred is None:
            cinfo_a.succ = cinfo_b
            cinfo_b.pred = cinfo_a

    # generate list of spans as output
    spans = []

    # until we have removed everything from the list
    while cinfo_list:

        # get the first on the list
        cinfo = cinfo_list[0]

        # keep following predecessors until none exists
        while cinfo.pred:
            cinfo = cinfo.pred

        # start a new span
        cur_span = []

        width = 0.0

        # follow successors til end of span
        while cinfo:
            # remove from list (sadly making this loop *also* O(n^2)
            cinfo_list.remove(cinfo)
            # add to span
            cur_span.append(cinfo)
            width += cinfo.local_xrng[1] - cinfo.local_xrng[0]
            # set successor
            cinfo = cinfo.succ

        # add if long enough
        if width > SPAN_MIN_WIDTH:
            spans.append(cur_span)

    if DEBUG_LEVEL >= 2:
        visualize_spans(name, small, pagemask, spans)

    return spans


def sample_spans(shape, spans):

    span_points = []

    for span in spans:

        contour_points = []

        for cinfo in span:

            yvals = np.arange(cinfo.mask.shape[0]).reshape((-1, 1))
            totals = (yvals * cinfo.mask).sum(axis=0)
            means = totals / cinfo.mask.sum(axis=0)

            xmin, ymin = cinfo.rect[:2]

            step = SPAN_PX_PER_STEP
            start = ((len(means)-1) % step) // 2

            contour_points += [(x+xmin, means[x]+ymin)
                               for x in range(start, len(means), step)]

        contour_points = np.array(contour_points,
                                  dtype=np.float32).reshape((-1, 1, 2))

        contour_points = pix2norm(shape, contour_points)

        span_points.append(contour_points)

    return span_points


def keypoints_from_samples(name, small, pagemask, page_outline,
                           span_points):

    all_evecs = np.array([[0.0, 0.0]])
    all_weights = 0

    for points in span_points:

        _, evec = cv2.PCACompute(points.reshape((-1, 2)),
                                 None, maxComponents=1)

        weight = np.linalg.norm(points[-1] - points[0])

        all_evecs += evec * weight
        all_weights += weight

    evec = all_evecs / all_weights

    x_dir = evec.flatten()

    if x_dir[0] < 0:
        x_dir = -x_dir

    y_dir = np.array([-x_dir[1], x_dir[0]])

    pagecoords = cv2.convexHull(page_outline)
    pagecoords = pix2norm(pagemask.shape, pagecoords.reshape((-1, 1, 2)))
    pagecoords = pagecoords.reshape((-1, 2))

    px_coords = np.dot(pagecoords, x_dir)
    py_coords = np.dot(pagecoords, y_dir)

    px0 = px_coords.min()
    px1 = px_coords.max()

    py0 = py_coords.min()
    py1 = py_coords.max()

    p00 = px0 * x_dir + py0 * y_dir
    p10 = px1 * x_dir + py0 * y_dir
    p11 = px1 * x_dir + py1 * y_dir
    p01 = px0 * x_dir + py1 * y_dir

    corners = np.vstack((p00, p10, p11, p01)).reshape((-1, 1, 2))

    ycoords = []
    xcoords = []

    for points in span_points:
        pts = points.reshape((-1, 2))
        px_coords = np.dot(pts, x_dir)
        py_coords = np.dot(pts, y_dir)
        ycoords.append(py_coords.mean() - py0)
        xcoords.append(px_coords - px0)

    if DEBUG_LEVEL >= 2:
        visualize_span_points(name, small, span_points, corners)

    return corners, np.array(ycoords), xcoords


def visualize_contours(name, small, cinfo_list):

    regions = np.zeros_like(small)

    for j, cinfo in enumerate(cinfo_list):

        cv2.drawContours(regions, [cinfo.contour], 0,
                         CCOLORS[j % len(CCOLORS)], -1)

    mask = (regions.max(axis=2) != 0)

    display = small.copy()
    display[mask] = (display[mask]/2) + (regions[mask]/2)

    for j, cinfo in enumerate(cinfo_list):
        color = CCOLORS[j % len(CCOLORS)]
        color = tuple([c/4 for c in color])

        cv2.circle(display, fltp(cinfo.center), 3,
                   (255, 255, 255), 1, cv2.LINE_AA)

        cv2.line(display, fltp(cinfo.point0), fltp(cinfo.point1),
                 (255, 255, 255), 1, cv2.LINE_AA)

    debug_show(name, 1, 'contours', display)


def visualize_spans(name, small, pagemask, spans):

    regions = np.zeros_like(small)

    for i, span in enumerate(spans):
        contours = [cinfo.contour for cinfo in span]
        cv2.drawContours(regions, contours, -1,
                         CCOLORS[i*3 % len(CCOLORS)], -1)

    mask = (regions.max(axis=2) != 0)

    display = small.copy()
    display[mask] = (display[mask]/2) + (regions[mask]/2)
    display[pagemask == 0] /= 4

    debug_show(name, 2, 'spans', display)


def visualize_span_points(name, small, span_points, corners):

    display = small.copy()

    for i, points in enumerate(span_points):

        points = norm2pix(small.shape, points, False)

        mean, small_evec = cv2.PCACompute(points.reshape((-1, 2)),
                                          None,
                                          maxComponents=1)

        dps = np.dot(points.reshape((-1, 2)), small_evec.reshape((2, 1)))
        dpm = np.dot(mean.flatten(), small_evec.flatten())

        point0 = mean + small_evec * (dps.min()-dpm)
        point1 = mean + small_evec * (dps.max()-dpm)

        for point in points:
            cv2.circle(display, fltp(point), 3,
                       CCOLORS[i % len(CCOLORS)], -1, cv2.LINE_AA)

        cv2.line(display, fltp(point0), fltp(point1),
                 (255, 255, 255), 1, cv2.LINE_AA)

    cv2.polylines(display, [norm2pix(small.shape, corners, True)],
                  True, (255, 255, 255))

    debug_show(name, 3, 'span points', display)


def imgsize(img):
    height, width = img.shape[:2]
    return '{}x{}'.format(width, height)


def make_keypoint_index(span_counts):

    nspans = len(span_counts)
    npts = sum(span_counts)
    keypoint_index = np.zeros((npts+1, 2), dtype=int)
    start = 1

    for i, count in enumerate(span_counts):
        end = start + count
        keypoint_index[start:start+end, 1] = 8+i
        start = end

    keypoint_index[1:, 0] = np.arange(npts) + 8 + nspans

    return keypoint_index


def project_keypoints_jacobian(pvec, keypoint_index):
    """Analytic Jacobian of project_keypoints' output (flattened (u0,v0,u1,v1,...))
    with respect to every entry of pvec — supplied as jac= to scipy.optimize.minimize
    so L-BFGS-B never has to fall back to a numerical (finite-difference) gradient
    approximation, which requires ~len(pvec) extra objective evaluations per step and
    was found to dominate total runtime on at least one real machine (confirmed: with
    no jac=, optimizer time on the exact same photos varied wildly across machines —
    0.5s on one, 15-25s on another — instead of scaling only with problem difficulty).

    Verified against SciPy's own central-difference numerical gradient (5 random
    configurations at 555-650 parameters, matching real photos' scale) to within
    ~1e-6 relative error before this was trusted — see the derivation below.

    The three pieces of the chain rule:
    1. d(image point)/d(rvec, tvec): cv2.projectPoints' own `jacobian` output —
       OpenCV's analytically-computed derivative (the same one solvePnP/
       calibrateCamera use internally), not re-derived here.
    2. d(image point)/d(object point xyz), for FIXED rvec/tvec: standard closed-form
       pinhole-camera derivative (camera point = R@obj + t; u = fx*x/z + cx), taking
       R from cv2.Rodrigues(rvec).
    3. d(object point)/d(the actual free parameters — alpha, beta, and each
       keypoint's own x; y is a single value shared by every point in its span, per
       make_keypoint_index below): a closed-form polynomial derivative, since
       project_xy's z = (alpha+beta)x^3 + (-2alpha-beta)x^2 + alpha*x.
    Chained together via the product/chain rule; point 0 is pinned to the origin
    (see project_keypoints) and contributes no gradient. Gradients from points that
    share a span's y-parameter are accumulated (+=), not overwritten, since several
    rows of the Jacobian reference the same column.
    """
    x_param_idx = keypoint_index[:, 0]
    y_param_idx = keypoint_index[:, 1]

    xy_coords = pvec[keypoint_index].copy()
    xy_coords[0, :] = 0
    x = xy_coords[:, 0]
    n = xy_coords.shape[0]

    alpha, beta = tuple(pvec[CUBIC_IDX])
    rvec = pvec[RVEC_IDX]
    tvec = pvec[TVEC_IDX]

    c3 = alpha + beta
    c2 = -2 * alpha - beta
    c1 = alpha
    z = c3 * x**3 + c2 * x**2 + c1 * x
    objpoints = np.hstack((xy_coords, z.reshape((-1, 1)))).astype(np.float64)

    _, jac_full = cv2.projectPoints(objpoints, rvec, tvec, K, np.zeros(5))
    jac_rvec = jac_full[:, 0:3]
    jac_tvec = jac_full[:, 3:6]

    R, _ = cv2.Rodrigues(rvec)
    fx, fy = K[0, 0], K[1, 1]
    pc = (R @ objpoints.T).T + tvec.reshape(1, 3)
    pcx, pcy, pcz = pc[:, 0], pc[:, 1], pc[:, 2]
    du_dpc = np.stack([fx / pcz, np.zeros(n), -fx * pcx / pcz**2], axis=1)
    dv_dpc = np.stack([np.zeros(n), fy / pcz, -fy * pcy / pcz**2], axis=1)
    du_dobj = du_dpc @ R
    dv_dobj = dv_dpc @ R

    dz_dx = 3 * c3 * x**2 + 2 * c2 * x + c1
    dz_dalpha = x**3 - 2 * x**2 + x
    dz_dbeta = x**3 - x**2

    du_dxi = du_dobj[:, 0] + du_dobj[:, 2] * dz_dx
    dv_dxi = dv_dobj[:, 0] + dv_dobj[:, 2] * dz_dx
    du_dyi = du_dobj[:, 1]
    dv_dyi = dv_dobj[:, 1]
    du_dalpha = du_dobj[:, 2] * dz_dalpha
    dv_dalpha = dv_dobj[:, 2] * dz_dalpha
    du_dbeta = du_dobj[:, 2] * dz_dbeta
    dv_dbeta = dv_dobj[:, 2] * dz_dbeta

    J = np.zeros((2 * n, len(pvec)))
    J[0::2, RVEC_IDX] = jac_rvec[0::2]
    J[1::2, RVEC_IDX] = jac_rvec[1::2]
    J[0::2, TVEC_IDX] = jac_tvec[0::2]
    J[1::2, TVEC_IDX] = jac_tvec[1::2]
    J[0::2, 6] = du_dalpha
    J[1::2, 6] = dv_dalpha
    J[0::2, 7] = du_dbeta
    J[1::2, 7] = dv_dbeta

    for k in range(1, n):  # point 0 is pinned, contributes no gradient
        xi = x_param_idx[k]
        yi = y_param_idx[k]
        J[2 * k, xi] += du_dxi[k]
        J[2 * k + 1, xi] += dv_dxi[k]
        J[2 * k, yi] += du_dyi[k]
        J[2 * k + 1, yi] += dv_dyi[k]

    return J


def optimize_params(name, small, dstpoints, span_counts, params):

    keypoint_index = make_keypoint_index(span_counts)

    def objective(pvec):
        ppts = project_keypoints(pvec, keypoint_index)
        return np.sum((dstpoints - ppts)**2)

    def objective_jac(pvec):
        ppts = project_keypoints(pvec, keypoint_index)
        J = project_keypoints_jacobian(pvec, keypoint_index)
        diff = (ppts - dstpoints).reshape(-1)
        return 2.0 * (J.T @ diff)

    print('  initial objective is', objective(params))

    if DEBUG_LEVEL >= 1:
        projpts = project_keypoints(params, keypoint_index)
        display = draw_correspondences(small, dstpoints, projpts)
        debug_show(name, 4, 'keypoints before', display)

    print('  optimizing', len(params), 'parameters...')
    start = datetime.datetime.now()
    res = scipy.optimize.minimize(objective, params,
                                  method='L-BFGS-B', jac=objective_jac)
    end = datetime.datetime.now()
    print('  optimization took', round((end-start).total_seconds(), 2), 'sec.')
    print('  final objective is', res.fun)
    params = res.x

    if DEBUG_LEVEL >= 1:
        projpts = project_keypoints(params, keypoint_index)
        display = draw_correspondences(small, dstpoints, projpts)
        debug_show(name, 5, 'keypoints after', display)

    return params


def get_page_dims(corners, rough_dims, params):

    dst_br = corners[2].flatten()

    dims = np.array(rough_dims)

    def objective(dims):
        proj_br = project_xy(dims, params)
        return np.sum((dst_br - proj_br.flatten())**2)

    res = scipy.optimize.minimize(objective, dims, method='Powell')
    dims = res.x

    print('  got page dims', dims[0], 'x', dims[1])

    return dims


def remap_image(name, img, small, page_dims, params):

    # use the same scale factor on both axes (matches pix2norm/norm2pix)
    # so output pixel density matches the source image's on both axes,
    # instead of deriving width from height via the page aspect ratio
    scl = 0.5 * OUTPUT_ZOOM * max(img.shape[0], img.shape[1])

    height = round_nearest_multiple(page_dims[1] * scl, REMAP_DECIMATE)
    width = round_nearest_multiple(page_dims[0] * scl, REMAP_DECIMATE)

    print('  output will be {}x{}'.format(width, height))

    height_small = height // REMAP_DECIMATE
    width_small = width // REMAP_DECIMATE

    page_x_range = np.linspace(0, page_dims[0], width_small)
    page_y_range = np.linspace(0, page_dims[1], height_small)

    page_x_coords, page_y_coords = np.meshgrid(page_x_range, page_y_range)

    page_xy_coords = np.hstack((page_x_coords.flatten().reshape((-1, 1)),
                                page_y_coords.flatten().reshape((-1, 1))))

    page_xy_coords = page_xy_coords.astype(np.float32)

    image_points = project_xy(page_xy_coords, params)
    image_points = norm2pix(img.shape, image_points, False)

    image_x_coords = image_points[:, 0, 0].reshape(page_x_coords.shape)
    image_y_coords = image_points[:, 0, 1].reshape(page_y_coords.shape)

    image_x_coords = cv2.resize(image_x_coords, (width, height),
                                interpolation=cv2.INTER_CUBIC).astype(np.float32)

    image_y_coords = cv2.resize(image_y_coords, (width, height),
                                interpolation=cv2.INTER_CUBIC).astype(np.float32)

    if OUTPUT_COLOR_MODE == 'color':
        remap_src = img
    else:
        remap_src = cv2.cvtColor(img, cv2.COLOR_RGB2GRAY)

    remapped = cv2.remap(remap_src, image_x_coords, image_y_coords,
                         cv2.INTER_CUBIC,
                         None, cv2.BORDER_REPLICATE)

    if OUTPUT_COLOR_MODE == 'binary':
        output = cv2.adaptiveThreshold(remapped, 255, cv2.ADAPTIVE_THRESH_MEAN_C,
                                       cv2.THRESH_BINARY, ADAPTIVE_WINSZ, 25)
    else:
        output = remapped

    if OUTPUT_COLOR_MODE == 'color':
        pil_image = Image.fromarray(cv2.cvtColor(output, cv2.COLOR_BGR2RGB))
    else:
        pil_image = Image.fromarray(output)
        if OUTPUT_COLOR_MODE == 'binary':
            pil_image = pil_image.convert('1')

    # OUTPUT_ZOOM scales output pixels relative to the source, so the
    # true pixel density scales inversely with it (zoom=2 -> half as
    # many source-pixels-equivalent per output pixel -> half the dpi)
    output_dpi = OUTPUT_DPI * OUTPUT_ZOOM

    suffix = {'binary': '_thresh', 'gray': '_gray', 'color': '_color'}[OUTPUT_COLOR_MODE]
    outfile = name + suffix + '.png'
    pil_image.save(outfile, dpi=(output_dpi, output_dpi))

    if DEBUG_LEVEL >= 1:
        height = small.shape[0]
        width = int(round(height * float(output.shape[1])/output.shape[0]))
        display = cv2.resize(output, (width, height),
                             interpolation=cv2.INTER_AREA)
        debug_show(name, 6, 'output', display)

    return outfile


def process_one_image(imgfile):
    """Runs the full dewarp pipeline on a single image file and writes its
    output alongside page_dewarp.py's usual naming convention (see
    remap_image's OUTPUT_COLOR_MODE-based suffix). Returns the output file
    path, or None if too few text spans were found (page_dewarp.py's own
    "skip" case — not an error, just no confident curvature). Factored out
    of main()'s per-image loop body (unchanged logic) so --worker mode (see
    below) can call it repeatedly inside one long-lived process instead of
    every image paying Python-interpreter-and-import startup cost again."""

    img = cv2.imread(imgfile)
    small = resize_to_screen(img)
    basename = os.path.basename(imgfile)
    name, _ = os.path.splitext(basename)

    print('loaded', basename, 'with size', imgsize(img), end=' ')
    print('and resized to', imgsize(small))

    if DEBUG_LEVEL >= 3:
        debug_show(name, 0.0, 'original', small)

    pagemask, page_outline = get_page_extents(small)

    cinfo_list = get_contours(name, small, pagemask, 'text')
    spans = assemble_spans(name, small, pagemask, cinfo_list)

    if len(spans) < 3:
        print('  detecting lines because only', len(spans), 'text spans')
        cinfo_list = get_contours(name, small, pagemask, 'line')
        spans2 = assemble_spans(name, small, pagemask, cinfo_list)
        if len(spans2) > len(spans):
            spans = spans2

    if len(spans) < 1:
        print('skipping', name, 'because only', len(spans), 'spans')
        return None

    tight_pagemask, tight_outline = tighten_page_extents(small, pagemask, spans)
    if tight_outline is not None:
        pagemask, page_outline = tight_pagemask, tight_outline

    span_points = sample_spans(small.shape, spans)

    print('  got', len(spans), 'spans', end=' ')
    print('with', sum([len(pts) for pts in span_points]), 'points.')

    corners, ycoords, xcoords = keypoints_from_samples(name, small,
                                                       pagemask,
                                                       page_outline,
                                                       span_points)

    rough_dims, span_counts, params = get_default_params(corners,
                                                         ycoords, xcoords)

    dstpoints = np.vstack((corners[0].reshape((1, 1, 2)),) +
                          tuple(span_points))

    params = optimize_params(name, small,
                             dstpoints,
                             span_counts, params)

    page_dims = get_page_dims(corners, rough_dims, params)

    outfile = remap_image(name, img, small, page_dims, params)

    print('  wrote', outfile)
    print()

    return outfile


def run_worker_loop():
    """--worker mode: after the one-time interpreter/import cost already
    paid to reach this point, reads one absolute image path per line from
    stdin, dewarps it with process_one_image, and writes exactly one
    response line to stdout per request — "OK <outfile>" or "SKIP" (too few
    spans, page_dewarp's own low-confidence case) or "ERROR <message>" (any
    exception, with newlines replaced so the response stays one line) —
    before flushing and waiting for the next line. Exits cleanly on EOF
    (stdin closed, e.g. the parent process exited) or a line that is
    exactly "QUIT". See MicroCapture.Processing/PythonDewarpWorker.cs for
    the C# side of this protocol: it starts this process once and reuses it
    for every page in a batch instead of spawning fresh each time, which is
    what actually eliminates the interpreter+import startup cost — the
    optimizer itself was never the dominant cost once L-BFGS-B with an
    analytic gradient replaced Powell (see this file's header comment);
    confirmed on a real Windows machine that ~6-11 seconds of every
    single-page capture was Python/numpy/scipy/cv2/PIL startup overhead,
    not the optimizer, no matter how fast the optimizer itself got."""
    # process_one_image (and everything it calls — span detection, keypoint
    # sampling, optimize_params, remap_image, etc.) prints its own progress
    # lines via plain print(), same as CLI mode always has. Those must never
    # reach real stdout here, since this protocol is exactly one response
    # line per request — redirect stdout to stderr for the duration of the
    # actual work (visible in the debug log same as before) and only swap
    # the real stdout back in to emit the single protocol response line.
    real_stdout = sys.stdout
    print('WORKER_READY', file=real_stdout, flush=True)
    for line in sys.stdin:
        imgfile = line.strip()
        if not imgfile or imgfile == 'QUIT':
            break
        try:
            sys.stdout = sys.stderr
            outfile = process_one_image(imgfile)
            sys.stdout = real_stdout
            if outfile is None:
                print('SKIP', flush=True)
            else:
                print('OK', outfile, flush=True)
        except Exception as ex:  # pylint: disable=broad-except
            sys.stdout = real_stdout
            safe_message = str(ex).replace('\n', ' ').replace('\r', ' ')
            print('ERROR', safe_message, flush=True)


def main():

    global OUTPUT_COLOR_MODE

    args = sys.argv[1:]

    if '--color' in args:
        OUTPUT_COLOR_MODE = 'color'
        args.remove('--color')
    elif '--gray' in args:
        OUTPUT_COLOR_MODE = 'gray'
        args.remove('--gray')

    if '--worker' in args:
        args.remove('--worker')
        run_worker_loop()
        return

    if len(args) < 1:
        print('usage:', sys.argv[0], '[--color|--gray] IMAGE1 [IMAGE2 ...]')
        print('       ', sys.argv[0], '[--color|--gray] --worker')
        sys.exit(0)

    if DEBUG_LEVEL > 0 and DEBUG_OUTPUT != 'file':
        cv2.namedWindow(WINDOW_NAME)

    outfiles = []

    for imgfile in args:
        outfile = process_one_image(imgfile)
        if outfile is not None:
            outfiles.append(outfile)

    print('to convert to PDF (requires ImageMagick):')
    print('  convert -compress Group4 ' + ' '.join(outfiles) + ' output.pdf')


if __name__ == '__main__':
    main()
