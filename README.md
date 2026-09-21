# PoEformance (C# port)

A **reverse-engineering workbench for Path of Exile 2 that also renders overlays**.
C# / .NET 10, ImGui overlay, WebView2 config UI, Native AOT deployment.

This is the ground-up successor of the AutoHotkey v2 tool
([imm0r/PoEformance](https://github.com/imm0r/PoEformance)). The domain knowledge
(offsets, drift history, decoders) carries over; the architecture does not — it is
built around three ideas the old tool could not have:

1. **Offsets are data, not code** — `schema/poe2.offsets.json` holds every struct
   layout *with invariants*; a validator turns silent offset drift into a red row in
   an attach-time report, and the schema hot-reloads without a rebuild.
2. **Every read is recordable** — sessions capture to a file and replay identically,
   so decoders and features are developed and tested without the game running.
3. **Layers the compiler enforces** — six projects, references point strictly down,
   everything wired by hand in one `Program.cs`. No plugin system, no DI container,
   no reflection.

Read [`docs/architecture.md`](docs/architecture.md) — it is short and it is the map.

## Status

**Milestone 1 — vertical slice: done.**
Attach → pattern scan → schema-driven pointer walk → invariant validation →
drift report, with `--record` / `--replay`. The full pipeline is covered by tests
that run on any OS (30 passing), including two end-to-end tests against a synthetic
game process and a record→replay round trip.

Next: the struct viewer (schema-annotated live memory, hot reload), then watch
expressions and the continuous differ. Overlays after that; automation last.

## Layout

```
schema/poe2.offsets.json      the RE knowledge: offsets + invariants + drift history
src/PoEformance.Core          attach, RPM, patterns, schema, record/replay  (any OS)
src/PoEformance.Game          PoE2 domain: entities, player, terrain        (any OS)
src/PoEformance.Features      feature logic - data in, data out             (any OS)
src/PoEformance.Overlay       ImGui in-game overlay                     (Windows)
src/PoEformance.Config        WebView2 config window                    (Windows)
src/PoEformance.App           composition root - read Program.cs        (Windows)
tests/                        runs against synthetic memory / recordings (any OS)
docs/architecture.md          why it is built this way
```

## Build & run

Windows quick start (full guide: [`docs/setup-windows.md`](docs/setup-windows.md)):

```powershell
# one-time: install the .NET 10 SDK, then
git clone https://github.com/imm0r/PoEformance2.git

# daily, from an elevated PowerShell in the repo:
.\scripts\run.ps1            # pull + incremental build + attach + drift report
.\scripts\run.ps1 -Watch     # stay attached; re-validate on every schema save
```

Offset changes never need a build: edit `schema/poe2.offsets.json` while `-Watch`
runs and the report refreshes against the still-attached game. No SDK at all? Every
push to `main` auto-compiles on GitHub and updates the rolling release
[`latest-dev`](https://github.com/imm0r/PoEformance2/releases/tag/latest-dev) with a
ready-to-run, self-contained exe.

**The overlay says which build it is**, on its own title bar beside the lock: `v0.1.11 · 4659c18`
— the version, raised on every push, and the commit the publish workflow stamped into it. A build
compiled locally reads `local`, because a version number on an unreleased build is a claim it
cannot support. It is there because the alternative was comparing a screenshot against a merge
timestamp to find out whether a fix was in the build somebody was looking at.

**A downloaded build keeps itself current.** It checks that release a couple of times a day,
says so on the config window's **Update** tab and in the overlay's Status page, shows what
changed since the build you are running, and — when you press the button — downloads it,
unpacks it over its own folder and restarts into it, telling you it worked. Nothing is
downloaded or replaced without a click, `config/` is never touched, and the check can be
switched off on the same tab. Builds compiled locally say "local build" and are never
offered an update, because a release zip has no business landing on top of a working tree.

```bash
dotnet build        # any OS
dotnet test         # any OS
PoEformance.App --record session.rec     # capture a session (Windows)
PoEformance.App --replay session.rec     # develop against it, game closed (any use)
PoEformance.App --overlay --config       # in-game overlay + config window, side by side
PoEformance.App --overlay --debug        # plus the projection diagnostics and calibration aids
```

The overlay draws the area's layout **on the game's own map** — including the parts not
explored yet — and marks living monsters, chests, drops and NPCs on it. The large map when
it is open, the minimap otherwise, clipped to whichever is on screen. It
draws only while the game is the window in front (it is always-on-top, so anything painted
after an alt-tab would land on whatever you switched to) and only in endgame areas — not in
town, a hideout, or a campaign zone. Corpses are filtered out, and drops below magic rarity with them
(currency is never hidden); the threshold is in the config window. `--debug` brings back the
RE instruments — dots out in the 3D world, the projection measurements, the calibration
markers, and per-kind filters including terrain and effects.

A **boss arena is found in the shape of the ground** rather than among the entities — an endgame
map is generated at random and a boss room is not — so it is marked from the moment the area
loads, long before the boss exists to be read. Where the icon sheet carries the game's own
picture of that boss, the marker wears it: which picture is derived from the area's id and the
arena tile's path and only ever accepted when the sheet really has a cell under that name, with
`data/boss-icons.json` for the pairs the names do not settle. Arenas nothing could name are
collected in `logs/boss-arenas.tsv`, so that file gets filled from what was actually played.
Once the boss is down the marker switches to the Inactive art, the way the game's own landmarks
do. The switch is on the Markers → Map & Places tab.

**Where the game draws no icon for a boss, one is made from the boss itself.** The Monster Book's
model pane reads the mesh, skins and rig out of the game's own bundles — **skins**, plural, because
a monster is built of parts and each part wears its own sheet: the `.ao` files a material under
every shape's name and `SkinnedMesh` carries the same names, so body, cloak and wings are painted
from their own textures. One texture over the whole mesh is what drew Bahlak the Sky Seer black
with red patches, and a shape whose coordinates address another part's sheet samples whatever
happens to sit there — wrong rather than missing, which is the harder kind to notice. A monster
that names one material draws exactly as it did, and where it names one material with several
graphs in it, the number after the file picks which — that is what the game's own `Boss.mat:1`
means, and reading it is what stopped three bosses in a row wearing their head's sheet on their
cloak.

**Most bosses name their materials nowhere near their shapes, though, and the join is a number
nobody had identified.** Veynar the Frostbane is 35 shapes and his `.ao` names not one material;
his mesh manifest names three. Connal is 11 and 2, Count Geonor's human form 15 and 7 — and all
three came back painted from a single sheet and visibly in pieces. What joins the short list to
the long one is the number every manifest writes after a material's path, which the only other
reader of this format in the open calls `unk1`: it is how many consecutive shapes that material
covers. The file already in this repository's tests settles it — BasicSkeleton's manifest names
one material with the number 15, and its geometry, accounted for byte by byte, has exactly 15
shapes. Because one sample is a reading rather than a proof, the runs are used **only where they
add up to the shape count exactly**, and the pane says when they did, so every boss drawn this
way is another test of it rather than a result resting on the first.

A pose picked in that pane can be written straight out as icon art: colour and greyed, cut out on
transparency, at 64 px for a sheet cell and at 1024 to work on. Nothing is keyed by hand — the renderer's background was always
transparent, and the floor and the controls are drawn around the picture rather than into it. What
it wrote opens in a small window of its own, both halves at cell size and at three times it on a
checkerboard, because an icon is judged at 64 px and a pose that reads beautifully across the pane
can be a smudge in a cell. The
greying follows the game's own: fully desaturated, alpha untouched, and darker by a factor that
its 27 Active/Inactive boss pairs put between 0.47 and 1.14, which is why the pane carries a
slider with their median on it rather than one number. The black rim those icons have is
measured off the same 27 — the outermost pixel of the silhouette is black, the second is half
way out of it, and the art starts at the third — and the preview window puts one on, grown
outwards so the model keeps its detail or painted inwards the way the art was drawn, at a width
in cell pixels.

**The window that shows the picture also asks what it is of**, because that is the only moment
anybody has all three answers: the map, the arena tile and the boss's name. Three fields under the
previews, prefilled from where the player is standing, from the arenas collected in that area and
from the monster table's own name — and the write button then puts the entry in
`data/boss-icons.json` beside the four PNGs, in one click. One boss is often the boss of two maps,
so the area field takes a list. The name becomes the arena marker's label, in place of the one
derived from the tile file, and it takes effect the moment it is written rather than when the art
finally lands in the sheet.

**And the work that is left is a list rather than a discovery.** The set of endgame maps is known
exactly — `EndgameMaps.dat` is the table an atlas node points at, 173 rows — so the tool subtracts
what has a picture from what exists and shows the rest on the Markers → Map & Places tab, with the
arena tile it has already seen in each. Measured against the shipped files: **not one** of those
173 maps resolves a boss picture from its name. The sheet's 27 boss families are named for campaign
arenas and act bosses, and the game simply draws no minimap icon for its map bosses — which is what
the export above is for.

**Who stands there, though, the game does say — and that was missed for a while.** `WorldAreas` has
a `Bosses` column, a list of monsters per area, and it names the boss of **125** of the 173. So an
arena is labelled with its boss's real name from the moment the area loads, with nothing written
down; the icon family is the monster's own file name, which is what the export calls its pictures;
and one boss posed once covers every map it is the boss of. That last part is the whole saving:
those 125 maps hold **90** distinct bosses, **31** of which stand in more than one map — 66 maps
between them — and nothing in either map tells you the one you are in is a repeat. The 48 maps
that name no boss are exactly the hideouts, hubs, Expedition logbooks and merchant maps, so they
need no ticking off either. **And that column is now read from the client**, not only shipped: it is column 18 of
`WorldAreas.dat`, and the walk that already reads all 442 rows for their names and flags picks it
up in the same pass. The offset is arithmetic rather than a search, so what makes it believable is
that `data/area-bosses.json` was generated months earlier from a third-party export by a different
route — and a capture of the running client agrees with it on **206 of 442 rows, the same 206 ids,
and the same paths in the same order on every one**, with no disagreement in either direction and
nothing extra. Three things have to be right at once for that: the column offset, the array's
(count, pointer) reading, and which half of a foreign reference holds the row — and a mistake in
any of them does not produce 206 exact strings. `data/area-bosses.json` stays as
the answer until an atlas node has been seen and for any area the walk did not reach,
`data/boss-icons.json` overrides both where somebody has stood in the room,
and `scripts/area-bosses.py` is how the file is made. **Clicking the boss's name on a row opens the
Monster Book at that monster** and copies the name — finding it meant typing that name into a table
of 2733 rows, once per boss, ninety times. The list had hidden the Precursor towers as
boss-less on the way here; the column gives each of them two Reactor Guardians, which is the kind
of guess this project keeps promising itself it will stop making.

**And the brightness is matched, because a model is lit for a dungeon and an icon is painted for
a map.** The interior of the game's boss icons has a median luminance of 55; the first model
exported here measured 24, which on the minimap reads as a shadow of the icons beside it. So the
picture is brought to that number before the rim goes on — by a curve rather than a multiply, so
black stays black and a highlight is not clipped away, and by solving the exponent against the
picture's own histogram rather than its average, which misses by a third. What it actually
achieved is printed beside the slider: the exponent is found on luminance and applied per
channel, so it lands near the target rather than on it.

**The entity browser is two lists now, and a monster's page shows the monster.** An area holds a
few dozen monsters and several hundred of everything else — effects, doodads, projectiles,
terrain — so the rows anybody came for were a small minority of one list sorted by distance, and
finding the rare one meant typing part of its name. A toggle over the list splits it on the game's
own line (`EntityKind.Monster`, minions included) and carries the count of each side, so an area
with nothing alive in it says so without being scrolled. Each side keeps its own selected row,
because looking up what a ground effect is called and coming back to the monster you were reading
is the ordinary way through this.

And at the top of a monster's page the **model turns**, in a 180-pixel strip: the same renderer the
icon export uses, with everything around it taken off — no animation picker, no export, no drag,
no floor, and no button to stop the orbit. Every other answer on that page is a word, and none of
them tells you which of the fourteen things standing around you this row is. It loads only when a
row is picked, and it has its own picture ladder capped at the smallest rung, because a strip that
never grows has no business rasterising a megapixel.

**And a monster wears what the game dresses it in.** Doryani stands in the game in a skirt, a belt,
a necklace and six more pieces, and the pane drew him bare-legged — because none of that is in the
body mesh. Each is an `attached_object` naming its own `.ao`, mesh and rig, and the body's files say
nothing else about them. They are read now and **joined into one mesh** rather than drawn in turn,
which leaves the renderer, the per-shape palette and the pose working by construction instead of
through a second code path.

Where each piece goes is the socket its line names — `attached_object = "hip_jntBnd …/Skirt.ao"` —
and that had to be measured rather than assumed. Every one of Doryani's thirteen has a bounding box
a few tens of units across sitting on the origin, with the left and right shoulder pieces mirrored
in x rather than standing apart: they are modelled in their *own* space, so a piece means nothing in
the monster's until the socket bone's rest transform is on it. It is then bound rigidly to that one
bone, which is what makes it follow an arm that lifts. A piece hung on another piece sockets into
*its* rig rather than the body's — Doryani's dagger and mirror name bones of the belt — so an
unknown socket falls back to wherever its carrier went; at the top level, where there is no
carrier, the piece is left out instead, because bone 0 is not an answer but the floor. And
`<root>` is not a bone name at all: it is the game's way of saying *at my carrier's own origin*,
so the piece is already in that space and gets no transform. Reading it as an unknown bone put
the rig's root orientation on Bahlak the Sky Seer's feathers and laid them flat at his feet. A monster with no readable rig wears
nothing, because there is nowhere to put it and a pile of clothing at its feet is worse than none.
They are not free: nine pieces bring nine meshes, rigs and sheets, and a belt hangs three more under
itself, so the pane carries a `parts` switch and the line under the picture says `wearing 13 parts`.

It also draws **where a monster is pointing**. Path of Exile 2 keeps no target pointer anywhere in
memory — the game aims by *turning* an actor to an angle and firing once it is within a tolerance —
so the facing **is** the aim, as exactly as the game itself has it. The ray runs along it in world
units, a second one shows where it is turning to while it turns, and the animation beside it says
whether that is a slam starting or a monster walking past. Off by default: it costs the reader two
reads per monster, and it takes them back the moment it is switched off.

The **Tracker** tab (Combat page) carries the three features ported from the GameHelper2 plugin
[`hyper911/Tracker-GH2`](https://github.com/hyper911/Tracker-GH2): lines from the player to
unique/rare/magic monsters, rings around the ground effects you name by metadata path, and
buff/debuff icons over the player and over rare-or-better monsters — each with its stack count
and a timer bar drawn from the game's own remaining and total duration. The icons come from the
sheet that ships with the tool — the same one the map markers are cut from — and the tab has a
picker for it. The monster half is the one setting that costs a read
per monster, so it is off until switched on and takes its cost with it when switched off.

The config window has a **Map** tab showing the same layout at a readable size, with the
player and nearby markers on it. It stays open while the overlay runs and its settings apply
immediately — no restart. Auto flask is off until switched on there, per belt slot; the key each flask
uses is **read from the game's own config**, never assumed, and shown read-only beside the
slot it belongs to.

## License

MIT.
