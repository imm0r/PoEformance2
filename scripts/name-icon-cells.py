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

TWO TESTS, NOT ONE, and the second is what makes it trustworthy. A cell has to be
CLOSE to the file, and the runner-up cell has to be clearly worse. Closeness alone
accepts the Active and Inactive of the same landmark for each other - they differ by
a few pixels of glow, and there are dozens of such pairs - so a name would land on
whichever of the two the arithmetic happened to prefer. There is no recovering from
that by eye later, because both look right. Those stay unnamed.

The thresholds are read off the data rather than guessed. Every accepted match was
looked at as a contact sheet; at a distance under 16 the weakest of them is still
plainly the same picture, and the nearest rejections are exactly the Active/Inactive
pairs this is meant to refuse.

WHAT IS DELIBERATELY NOT HERE. No global assignment - pairing every file to a
different cell at minimum total cost would name the ambiguous pairs too, and would
name some of them backwards, which is the one outcome worse than leaving them blank.
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

    files, rows = [], []
    for path in sorted(icons.iterdir()):
        picture = Image.open(path).convert("RGBA")

        # An entirely transparent file names nothing: it sits a hair from every faint cell
        # and would take the best score in the whole run. There is one in the current set.
        if picture.getchannel("A").getbbox() is None:
            continue
        files.append((path.stem, flatten(picture)))

    probes = np.stack([f[1] for f in files])
    for start in range(0, len(probes), 64):
        block = np.abs(probes[start:start + 64, None, :] - sheet[None, :, :]).mean(axis=2)
        order = np.argsort(block, axis=1)
        for i, row in enumerate(order):
            best, runner = block[i, row[0]], block[i, row[1]]
            ratio = runner / best if best > 0 else float("inf")
            if best < NEAR and ratio >= CLEAR:
                rows.append((numbers[row[0]], files[start + i][0]))

    named = {}
    for cell, name in rows:
        # Cannot happen with the current set and is checked anyway: two files agreeing on
        # one cell means one of them is wrong, and silently keeping the last is how a wrong
        # name ships.
        if cell in named:
            sys.exit(f"cell {cell} claimed by both {named[cell]} and {name}")
        named[cell] = name

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


if __name__ == "__main__":
    main(sys.argv)
