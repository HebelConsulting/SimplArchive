"""Patch-code detection: which pages of a scanned batch are Kodak **Patch 3** separator sheets (issue #492).

Here rather than in the Api because this image is the only one in the deployment with a PDF rasteriser — the
Api image is Alpine (musl) and the usable rasterisers ship glibc-only natives, which is the same reason
`/thumbnail` lives here. Doing TIFF in-process and PDF in the sidecar would have meant two implementations of
one detector, so both formats come here.

The geometry, its sources, and what they imply are written up in `docs/reference/patch-codes.md`. The three
numbers that do the work, and that the freely-available summaries omit:

  * a bar is at least **2 in long** but the whole code is at most **0.80 in tall**;
  * wide : narrow is **2.5 : 1** — a ratio, because scanning DPI varies per scanner;
  * a **quiet zone** surrounds it.

Those are what separate a patch code from ordinary page furniture. Built from the bar widths alone, a detector
fires on any table with heavy horizontal rules.
"""
from dataclasses import dataclass

import numpy as np

# --- The spec, in inches (Kodak A-61599). -----------------------------------------------------------------
WIDE_IN = 0.20
NARROW_IN = 0.08
MIN_BAR_LENGTH_IN = 2.0
MAX_CODE_HEIGHT_IN = 0.80
QUIET_ZONE_IN = 0.5  # the spec says 10 narrow bars (0.8 in); relaxed, since we clip it at the page edge anyway

# 8-bit grey at or below this is ink. Bars are solid black on paper, so nothing subtler is needed: a bilevel
# scan is 0/255 and a colour scan lands around 40 on ink and 230 on paper.
INK = 128

# A bar row that carries more ink than this is not a bar — it is a full-width rule or a line of text that
# happens to contain a long dark run. Expressed against the page so it holds at any width.
MAX_BAR_INK_FRACTION = 0.75

# A row carrying at least this much ink counts as "content" for the two guards below.
CONTENT_INK_FRACTION = 0.02

# A separator sheet is nearly blank: a code, perhaps a title and a line of instructions. A page whose rows are
# mostly content is a DOCUMENT page, and this detector's caller DISCARDS what it flags — so the guard is what
# makes throwing the page away safe when somebody prints a patch code on real letterhead.
MAX_CONTENT_ROW_FRACTION = 0.20

# The code sits close to the feed edge. Anything mid-page is page furniture that happens to fit the geometry.
EDGE_BAND_FRACTION = 0.30


@dataclass(frozen=True)
class _Band:
    """One run of consecutive rows that each carry a single long horizontal ink run — i.e. a bar."""

    top: int
    bottom: int
    left: int
    length: int

    @property
    def height(self) -> int:
        return self.bottom - self.top + 1


def find_patch3(gray: np.ndarray, dpi: float) -> bool:
    """True when this page carries a Patch 3 code (wide, narrow, wide, narrow) near either feed edge."""
    height, width = gray.shape
    if height < 8 or width < 8:
        return False

    dark = gray <= INK
    ink_per_row = dark.sum(axis=1)

    content_rows = int((ink_per_row > CONTENT_INK_FRACTION * width).sum())
    if content_rows > MAX_CONTENT_ROW_FRACTION * height:
        return False  # a document page, not a separator sheet

    bands = _bands(dark, ink_per_row, dpi, width)
    for i in range(len(bands) - 3):
        if _is_patch3(bands[i:i + 4], ink_per_row, dpi, height, width):
            return True

    return False


def _bands(dark: np.ndarray, ink_per_row: np.ndarray, dpi: float, width: int) -> list[_Band]:
    """Group the rows that look like part of a bar into vertical bands."""
    minimum = int(MIN_BAR_LENGTH_IN * dpi * 0.8)  # a scan may clip the ends; ±20 % is generous but still long
    if minimum < 8 or minimum >= width:
        return []

    # Does a fully-dark run of `minimum` pixels exist in this row, and where does the leftmost one start? A
    # sliding window over the row's cumulative sum answers both at once, vectorised over every row.
    cumulative = np.zeros((dark.shape[0], width + 1), dtype=np.int32)
    np.cumsum(dark, axis=1, out=cumulative[:, 1:])
    window = cumulative[:, minimum:] - cumulative[:, :-minimum]
    longest = window.max(axis=1)
    leftmost = window.argmax(axis=1)

    is_bar = (longest >= minimum) & (ink_per_row <= MAX_BAR_INK_FRACTION * width)

    bands: list[_Band] = []
    y = 0
    while y < len(is_bar):
        if not is_bar[y]:
            y += 1
            continue

        top = y
        while y < len(is_bar) and is_bar[y]:
            y += 1

        bands.append(_Band(
            top=top,
            bottom=y - 1,
            left=int(np.median(leftmost[top:y])),
            # Total ink in the row IS the bar's length, because a row inside the quiet zone carries nothing
            # else. Cheaper than measuring the run, and the quiet-zone check below is what makes it true.
            length=int(np.median(ink_per_row[top:y])),
        ))

    return bands


def _is_patch3(group: list[_Band], ink_per_row: np.ndarray, dpi: float, height: int, width: int) -> bool:
    narrow = NARROW_IN * dpi

    # Three spaces, each a narrow bar wide. Wide tolerances: thresholding eats or adds a row at each edge.
    gaps = [group[j + 1].top - group[j].bottom - 1 for j in range(3)]
    if any(gap < narrow * 0.4 or gap > narrow * 2.2 for gap in gaps):
        return False

    if group[3].bottom - group[0].top + 1 > MAX_CODE_HEIGHT_IN * dpi * 1.4:
        return False

    # Four bars of one code are drawn on top of each other: same left edge, same length. A stack of unrelated
    # rules is not, which is the cheapest discriminator there is.
    lefts = [band.left for band in group]
    lengths = [band.length for band in group]
    if max(lefts) - min(lefts) > 0.15 * dpi:
        return False
    if max(lengths) > 1.35 * min(lengths) or min(lengths) < MIN_BAR_LENGTH_IN * dpi * 0.8:
        return False

    # The RATIO, not the absolute heights: nominal 2.5 : 1, and the DPI we were handed may be an estimate.
    heights = [band.height for band in group]
    thin, thick = min(heights), max(heights)
    if thin < 1 or not 1.7 <= thick / thin <= 3.6:
        return False

    midpoint = (thin + thick) / 2
    pattern = [h > midpoint for h in heights]

    # **Read from the nearest page edge inward.** A sheet carries the code at the top AND the bottom so that a
    # page fed 180 degrees round still presents one at the lead edge — and a code drawn from the bottom edge
    # runs upward. Read top-down regardless and every bottom-edge code decodes as its own reverse, which for
    # Patch 3 (W N W N) is exactly Patch T (N W N W): a different instruction, silently obeyed.
    centre = (group[0].top + group[3].bottom) / 2
    if centre > height / 2:
        pattern = pattern[::-1]

    if pattern != [True, False, True, False]:
        return False

    if not (centre < EDGE_BAND_FRACTION * height or centre > (1 - EDGE_BAND_FRACTION) * height):
        return False

    return _quiet(ink_per_row, group[0].top, group[3].bottom, dpi, height, width)


def _quiet(ink_per_row: np.ndarray, top: int, bottom: int, dpi: float, height: int, width: int) -> bool:
    """Clear space above and below. Clipped at the page edges, since a code 0.5 in in has half of it off-page."""
    margin = int(QUIET_ZONE_IN * dpi)
    limit = CONTENT_INK_FRACTION * width

    above = ink_per_row[max(0, top - margin):top]
    below = ink_per_row[bottom + 1:min(height, bottom + 1 + margin)]

    return not (above > limit).any() and not (below > limit).any()
