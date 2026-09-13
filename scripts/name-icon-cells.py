#!/usr/bin/env python3
"""Name the cells of assets/icons.png from the standalone icons poe2db publishes.

WHY THIS IS A SCRIPT AND NOT A HAND-WRITTEN LIST. The sheet holds 1050 pictures and
nothing in it says what any of them is. The same art is also published one file per
icon, under names the game itself uses - so the names exist, they are just in the
other pile. This walks one pile past the other and writes down which is which.

Run it after fetching the icons:

    .\\scripts\\fetch-minimap-icons.ps1              # fills assets/minimap-icons/
    python3 scripts/name-icon-cells.py               # writes assets/icon-names.tsv

MATCHED BY SIMILARITY, NOT BY HASH, and that is measured rather than preferred: not
one of the 653 files is byte-identical to any cell, though both are 64x64 - the sheet
was recompressed on its way here. Cropping each cell and comparing pixels directly
works; comparing at 16x16 works as well, identifies just as surely, and turns 653 by
1050 comparisons into something that finishes in seconds.

EACH FILE MAKES ONE CLAIM, AND A CELL WITH ONE CLAIMANT IS DECIDED. Every file
proposes the single cell it is closest to, and nothing else; a cell nobody else
proposed is that file's, a cell several files proposed goes to the closest only if
the next claimant is clearly worse, and otherwise stays blank.

WHY THAT SHAPE, because the obvious two alternatives both get the hard case wrong.
The hard case is the Active and Inactive of one landmark: they differ by a few pixels
of glow, there are dozens of such pairs, and naming one backwards is the one outcome
worse than leaving it blank, since both look right afterwards.

  - Comparing a file against its own RUNNER-UP CELL refuses them. Active sits close
    to both of its cells, so its second-best is never clearly worse, and the pair is
    dropped - which cost 54 names that were perfectly decidable.
  - Asking whether the two are each other's best (mutual nearest) names them
    BACKWARDS. Measured, on Rootdredge: cell 703 is nearer to the Inactive file
    (6.11) than to the Active one (7.27), so the cell "prefers" Inactive - but
    Inactive is nearer still to cell 704 (4.79), which is its own. The cell's
    preference is not evidence; what the pair is ABOUT is which cell each file wants.

Claims settle it because the two files want DIFFERENT cells. Active claims 703,
Inactive claims 704, neither cell has another claimant, and both are named - by the
same rule that leaves a genuinely undecidable cell blank.

The thresholds are read off the data rather than guessed. Every accepted match was
looked at as a contact sheet; at a distance under 16 the weakest of them is still
plainly the same picture.

WHAT CONFIRMS IT, and it is worth more than the arithmetic. The pairs this resolves
land on ADJACENT cell numbers - 200/201, 629/630, 633/634, 705/706, 707/708, 709/710,
787/788, 793/794, 795/796 - always Active first. Nothing here knows that cells next
to each other are related; the comparison sees 16x16 pictures and no numbers at all.
The sheet agreeing with the matching about which of the two came first is a check the
matching could not have passed by being confidently wrong.

WHAT IS DELIBERATELY NOT HERE. No global assignment - pairing every file to a
different cell at minimum total cost would also fill in the genuinely undecidable
cells, and it reaches them by moving names off cells that were never in doubt.
A file whose claim loses does NOT fall back to its second choice, for the same reason.
"""

import pathlib
import sys

try:
    import numpy as np
    from PIL import Image
except ImportError:
    sys.exit("needs numpy and Pillow: pip install numpy Pillow")

TILE = 64
"""The sheet's cell size. See IconSheet in PoEformance.Features for how it was measured."""

COMPARE = 16
"""Both sides are shrunk to this before comparing. Enough to tell 1050 icons apart."""

NEAR = 16.0
"""Largest mean per-channel difference, on a 0-255 scale, that still counts as a match."""

CLEAR = 1.8
"""How much worse the runner-up cell has to be. Below this the match is not decidable."""


def cells(sheet):
    """Every cell holding art, as (index-from-one, image)."""
    across = sheet.width // TILE
    for row in range(sheet.height // TILE):
        for column in range(across):
            box = sheet.crop((column * TILE, row * TILE, (column + 1) * TILE, (row + 1) * TILE))
            if box.getchannel("A").getbbox() is not None:
                yield (row * across) + column + 1, box


def flatten(image):
    return np.asarray(image.resize((COMPARE, COMPARE), Image.LANCZOS), dtype=np.float32).ravel()


def main(argv):
    root = pathlib.Path(__file__).resolve().parent.parent
    sheet_path = root / "assets" / "icons.png"
    icons = pathlib.Path(argv[1]) if len(argv) > 1 else root / "assets" / "minimap-icons"
    out = pathlib.Path(argv[2]) if len(argv) > 2 else root / "assets" / "icon-names.tsv"

    if not icons.is_dir():
        sys.exit(f"no icons at {icons} - run scripts/fetch-minimap-icons.ps1 first")

    numbers, tiles = zip(*cells(Image.open(sheet_path).convert("RGBA")))
    sheet = np.stack([flatten(t) for t in tiles])

    files = []
    for path in sorted(icons.iterdir()):
        picture = Image.open(path).convert("RGBA")

        # An entirely transparent file names nothing: it sits a hair from every faint cell
        # and would take the best score in the whole run. There is one in the current set.
        if picture.getchannel("A").getbbox() is None:
            continue
        files.append((path.stem, flatten(picture)))

    # Blocked because the whole difference tensor is files x cells x pixels - two
    # gigabytes at this size, for an answer that is one float per pair.
    probes = np.stack([f[1] for f in files])
    distance = np.empty((len(files), len(numbers)), dtype=np.float32)
    for start in range(0, len(probes), 64):
        distance[start:start + 64] = np.abs(
            probes[start:start + 64, None, :] - sheet[None, :, :]).mean(axis=2)

    # One claim per file: the cell it is closest to, if that is close at all.
    claims = {}
    for i, cell in enumerate(np.argmin(distance, axis=1)):
        if distance[i, cell] < NEAR:
            claims.setdefault(int(cell), []).append(i)

    named, contested = {}, []
    for cell, who in claims.items():
        who.sort(key=lambda i: distance[i, cell])
        if len(who) == 1:
            named[numbers[cell]] = files[who[0]][0]
        elif distance[who[1], cell] >= distance[who[0], cell] * CLEAR:
            # Several files want this one cell and the nearest is clearly nearest. The
            # usual shape is a file whose own cell is not in the sheet at all, drifting
            # onto its closest relative.
            named[numbers[cell]] = files[who[0]][0]
        else:
            contested.append((numbers[cell], [files[i][0] for i in who]))

    head = [
        "# Internal names for cells of assets/icons.png, one per line: cell<TAB>name.",
        "# Cells count from ONE, the way a style file stores them.",
        "#",
        "# NOT hand-written and NOT complete - written by scripts/name-icon-cells.py, which",
        "# explains how the matching works and what it refuses to guess at.",
        f"# {len(named)} of {len(numbers)} occupied cells are named.",
    ]
    out.write_text("\n".join(head + [f"{c}\t{named[c]}" for c in sorted(named)]) + "\n")
    print(f"{out}: {len(named)} of {len(numbers)} cells named, from {len(files)} icons")
    for cell, who in contested:
        print(f"  cell {cell} left blank - {' and '.join(who)} are too alike to tell apart")


if __name__ == "__main__":
    main(sys.argv)
