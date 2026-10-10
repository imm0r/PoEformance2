# Project conventions

Path of Exile 2 memory-reading / overlay tool. C# on .NET 10, ImGui for the overlay,
WebView2 for configuration, Native AOT for shipping. A ground-up rewrite of the AutoHotkey
v2 tool, not a port of its structure.

## Never guess — read the reference

**When something about the game's memory or rendering is unclear, OPEN THE REFERENCE
before forming a theory.** This is the single most important rule here, and ignoring it has
already cost real time on this project.

- **`Gordin/GameHelper2`** (branch `main`) is the authority. It is a working tool against
  the same game, so its code answers questions that guessing cannot. Fetch the actual file:
  `https://raw.githubusercontent.com/Gordin/GameHelper2/main/<path>`.
  Especially useful: `GameHelper/RemoteObjects/States/InGameStateObjects/WorldData.cs`
  (the world-to-screen projection), `Plugins/Radar/` (the map projection and its Helper),
  `Plugins/HealthBars/` (how to place something over an entity), and
  `GameOffsets/` (struct layouts).
- **The AHK tool** (`imm0r/PoEformance`, `ahk/`) is battle-tested against PoE2 specifically
  and carries drift history the upstream does not. Its `CLAUDE.md` is a long record of
  problems already solved.
- **This tool's own interface browser** is a reference too, and the easiest one to forget
  because it is not a file: Inspect → UI Browser walks the live UI element tree, prints
  every element's StringId, rectangle, flags and child path, and F8 picks whatever is under
  the cursor. Any question of the form "does the game name that thing, and where is it" is
  one screenshot away — ask it there before concluding that something cannot be measured.
  The model pane has the same thing for its OWN pictures: the corner button "probe", then a
  click, lists every surface under that pixel (material, blend, program or texture, which
  doodad or tile piece, depths), and counts how each translucent layer came out over the
  whole picture. The capture key (F9, "Capture for Diagnosis") writes it to `model-probe.txt`
  in the capture's zip, beside every other diagnosis the tool can give — ask for that capture
  before theorising about why a tile or room looks wrong.
- **`adamthedash/poe_data_tools`** is to the game's FILE formats what GameHelper2 is to its
  memory: working parsers for `.ao`, `.sm`, `.smd`, `.fmt`, `.mat` and `.ast`. Clone it and
  read the parser, not a diagram of it — `crates/poe_data_tools-lib/src/file_parsers/`.
  `.smd` and `.fmt` wrap the SAME DOLm geometry block, and every rule about what sits between
  that block and the shape names is a `cond(...)` in its `dolm` parser. Reading those gates
  as unconditional cost this project a day: four bytes taken that were not there put every
  shape name four bytes out, and the names came back NEARLY right, which reads as a wrongly
  placed piece rather than a wrongly stepped file. It covers BOTH games — its CLI takes
  `--patch 1`, `2` or a specific version — so a parser there is a PoE2 parser unless it
  branches on the patch.
- **Know which game a reference was written against.** The two games share most file formats,
  which is exactly why a PoE1 tool can look like proof: it reads the PoE2 file and nearly
  works. Check its paths before citing it.
  - `annalithic/poeformats` is PoE2 now: `GameDirectory.cs` lists extracts 0.1.0 to 0.5.4f,
    the PoE2 releases, and `Schema` defaults to `poe2 = true`. Its `Ggpk.cs` still points at
    an old 0.8.8 extract, so a legacy corner of it is not.
  - `annalithic/poeterrain` is **Path of Exile 1 only**: every path names 3.17 Siege to 3.21
    Crucible, and its last commit is 2023-08-10. Its `.arm` importer helped (a slot as a fan of
    four corner quarters, slots at their own `(x, y)`), but nothing taken from it is settled for
    PoE2 until PoE2 files or the game agree. Its doodads prove nothing: the line that reads them
    (`Arm.cs`) and the loop that places them (`ArmImportComponent.cs`) are both commented out.
- Do not trust a summarised directory listing over the real thing. A tree summary once
  reported "no Radar plugin" for a repo that plainly has one, and that wrong answer was
  taken at face value.

What guessing produced, for the record: a matrix invariant that rejected the correct offset
and accepted a decoy; a projection "proven" by a check that a wrong matrix passes trivially;
a 52-pixel offset explained by an invented theory about HUD framing; markers moved onto
`TerrainHeight`, which belongs to a different coordinate system entirely; a hand-written
parser for the game's key bindings that missed both the `Input_flask_4_primary` spelling and
the fact that a numeric value is a decimal VIRTUAL-KEY CODE (`81` is Q, not the 8 and 1
keys); and a whole feature built to have the USER say where the HUD is, on the conclusion
that the game does not name its parts — while the tool's own browser lists the HUD as an
element called `HUD` with `life_orb`, `mana_orb` and `experience_bar` as its children. Each
was a one-minute lookup away.

That last one is the variant to watch for, because it does not feel like guessing: the
reference projects were checked, neither had an answer, and "not measurable" followed. **The
absence of an answer in the reference is not evidence of absence in the game.** GameHelper2's
Radar has the user drag a culling window over their own screen; that is what the reference
does, not what the game permits. Check what the game actually exposes before adopting
somebody else's workaround.

The game's own files are reference material too, not just the two projects above. The flask
keys live in `poe2_production_Config.ini`, so the tool reads them rather than assuming the
default 1-5 layout — the assumption looks correct until someone rebinds, and then the only
symptom is that nothing happens.

A monster's `.ao` is the same kind of file, and six versions went out learning it. Attached
pieces came out in the wrong place, and every fix was a rule invented from bone NAMES and
rest positions — any shared name, then a shared name resting where the parent's rests, then
the socket, then the nearest shared ancestor — while the answer sat in the piece's own `.ao`
the whole time: an `attachment_bones` line and the `bone_group` it ends with, two parallel
lists pairing the piece's bones to the parent's by POSITION in the list. Everything in the
middle agrees, which is why guessing by name got most of a monster right; the first entry
does not, which is why the rest of him hung off the floor. Two of the invented rules each
broke the monster the other had just fixed.

What finally showed the shape of it was printing the PARENT's rest position beside the
piece's in the model dump. The two rigs carry different rest poses — a piece is authored in a
neutral one and the body in a crouch — and four rules in a row had assumed they matched
without ever putting the two numbers side by side. **Print both halves of a comparison before
theorising about either**, and read the attachment's own file before its bone list.

A room's `.arm` is the same again. Each doodad line carries a counted list of floats that
neither reference names (poe_data_tools: `floats`, poeformats: `unk7`). It is the doodad's z
in the area's own frame. Two things in the game settled it: a pot read from memory sits at its
line's −115, and seepage's offices drew right once every doodad was set at its value rather than
on the ground. A doodad whose line carries no value stands on the ground. `RoomDoodad.Height`
holds the value, and the Tile Book's "doodad z" button keeps the two wrong readings for comparison.

## The two screen-space systems

The game projects to the screen in **two independent ways**, and mixing them up looks
exactly like a bug in the other one:

1. **The 3D world** — `WorldData.WorldToScreen(position, height)`, driven by the camera
   matrix at `WorldData + 0x1A0`. Clip components are dots with COLUMNS of the flat array.
   Entity positions come from `Render.WorldPosition`; its `Z` is the entity's BASE, and
   `Z - ModelBounds.Z` is where the game floats the health bar.
2. **The in-game map** — no matrix at all. A fixed 38.7-degree isometric transform whose
   scale comes from the map UI element's own zoom and shift. See the block comment above
   `ImportantUiElements` in `schema/poe2.offsets.json` for the formula.

Markers from (1) will never line up with the markers the game draws in (2), because the map
is zoomable. Comparing them is what makes a correct projection look broken.

**The game's frame is LEFT-HANDED** — world and model files alike, with up as minus z. The map
draws grid x up-right and y up-left (x to y anticlockwise from above, so x × y = −z), and every
rig puts its `L_` bones at +x while facing −y. A renderer built from proper rotations draws that
frame as its mirror image, which looks entirely right until it is held against the game and
cannot be turned to match: a room drawn from the area's own tiles came out exactly that way.
`MeshPicture.Camera` turns x over once for every picture the tool draws.

## Verify against the game, not against yourself

A check that a wrong value passes is worse than no check. Prefer tests the game itself can
settle:

- Project an entity's health-bar height and see whether it lands on the bar the game drew.
  That is a pixel-accurate reference, supplied free, on every monster on screen.
- For the camera matrix, require the player to be centred **and** the rest of the scene to
  spread out proportionally (`MatrixHunt`). Centring alone is satisfied by any matrix that
  inflates `w`, which collapses the whole scene onto one point.
- Structural fingerprints (a unit-length row, a plausible pointer) are weak. Frustum planes,
  basis blocks and inverse transforms all look like matrices.

## Offsets are data

`schema/poe2.offsets.json` is the reverse-engineering knowledge of this project. Edit it,
hot-reload with `--watch`, no rebuild. Every field may declare an invariant; the drift report
runs them at attach time. Record WHY an offset is what it is, and its drift history — that is
the part that is expensive to rediscover.

## Record, then diagnose offline

`--record` captures every read into a small file that replays without the game. Recordings
are the reason offsets can be diagnosed from Linux, and committed ones under `tests/fixtures/`
are regression tests against real memory. A recording can only contain reads the running
build actually performed, so a new diagnostic needs a fresh recording.

A capture (F9) is the same idea for a whole question: its zip holds the pictures, every
room file of the area's room set and every doodad entity with its model and position, so the
room placing runs again here without the game -
`POEF_CAPTURE=<unpacked folder> dotnet test tests/PoEformance.Core.Tests --filter CaptureReplay`
writes `placing-replay.txt` beside the capture's files. Two captures of one area forty
minutes apart are how it was learned that the power-line pieces and checkpoints come and go
with play while the plain props never move; hold a finder change against a capture before
asking for a screenshot. The reason they come and go is the network bubble: a scripted
object is the server's entity and the client holds it only while it is near, while the
props the client builds itself from the room files. A third capture had one checkpoint
entity in the client while the game's map drew four, and "there is one checkpoint in this
area" was nearly concluded from it. An entity's absence from the client says nothing. Its
presence does, so `DoodadMemory` keeps every scripted object the frame's read lists, by id,
for the rest of the instance, and the rooms are placed again when one arrives: a checkpoint
room is named once the player has been near its checkpoint, and stays named after.

**The loaded-file list is not the area.** It names what was loaded since the area change, and
a file still cached from an earlier instance of the same map is not loaded again and not
listed: The Assembly's list carried ten of its fifty-three rooms, neither the boss room nor
the entrance among them, and for a while "the boss room is not placed" was read as a placing
fault. The area's room set (`generate.rs`) names every room the generator may lay, and
`AreaRoomSet` places all of them. A fresh instance's list carries the set; a re-entered
instance is not generated again and its list carried the master (`master.tsi`) instead,
whose `RoomSet` line names the set; The Stone Citadel's list carried neither, both cached
from an earlier visit. So three roads are read: the listed sets, the listed masters, and
the masters the install's index lists in the folder above the rooms' `Rooms/` - where 456
of RePoE's 458 map graphs keep theirs. A list that lacks a thing is not evidence the area
lacks it.

## Raise the version on every push

`ToolVersion.Number` goes up by one patch with **every** push — one edit, one file, no
exceptions. It is drawn on the overlay's own title bar beside the lock, so "which build is
that" is a question a screenshot answers.

This exists because it cost an hour. Three changes went out in a row, two of them missed a
squash merge by under a minute, and a boss that looked unchanged could not be told apart from
a boss whose build did not contain the fix — the only way to check was comparing a screenshot
against a merge timestamp. A number that moves with every push makes that a glance.

The build stamp (`BuildStamp`, `version.json` beside the exe) still says which COMMIT is
running and is written by the publish workflow; the version says what was meant to be in it.
Both are shown, because they fail differently and a local build has only the version.

## Style

- Layering is compiler-enforced: Core → Game → Features → Overlay/Config → App, with Gpu
  (Game → Gpu → Overlay) beside Features. Nothing reaches backwards.
- Comments explain WHY, especially where a subtlety cost time. Do not narrate what the code
  already says.
- Analyzers are on and warnings are errors in spirit: keep the build at zero warnings.
- Chat may be German; everything committed is English.
