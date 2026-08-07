# Plates for the entry card

Three pictures, drawn as the plate above each weight's line when an area loads something
worth saying:

    preload-notable.png
    preload-valuable.png
    preload-dangerous.png

They are compiled INTO the assembly (`<EmbeddedResource>` in the csproj), so there is nothing
to install beside the executable and nothing to lose when a build is unzipped over an old
folder. Dropping a correctly named file in this folder is the whole job; the glob picks it up.

They are sliced from `assets/PreloadAlertsBanner.png` at the repo root, which is the master
sheet - keep editing that and cut it again rather than editing these.

What they need to be:

- **Transparent.** RGBA with a real alpha channel, trimmed to the artwork. A white or black
  background is drawn as a white or black box over the game.
- **Wide.** They are drawn at a third of the screen width, keeping their proportions, and
  shrunk to fit 1024 pixels on the longest edge. Anything much past that is detail nobody
  sees; anything under ~600 wide will look soft on a large monitor.
- **Legible small.** A plate ends up a few hundred pixels across in play. Fine runes and thin
  gold filigree turn to mush at that size - test before committing.

- **Built the same way**: emblem on the LEFT, open cloth to the right of it. The names are
  written into that cloth - centred between 42% and 94% of the width, at 45% of the height -
  so a plate whose ornament sits in the middle gets its text written across the ornament.

Missing or unreadable files are not an error: the card writes the weight's name in the
weight's colour instead, and a path set in the appearance editor beats whatever is here.
