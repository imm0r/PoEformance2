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
  because it is not a file: the Interface tree tab walks the live UI element tree, prints
  every element's StringId, rectangle, flags and child path, and F8 picks whatever is under
  the cursor. Any question of the form "does the game name that thing, and where is it" is
  one screenshot away — ask it there before concluding that something cannot be measured.
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

## Style

- Layering is compiler-enforced: Core → Game → Features → Overlay/Config → App. Nothing
  reaches backwards.
- Comments explain WHY, especially where a subtlety cost time. Do not narrate what the code
  already says.
- Analyzers are on and warnings are errors in spirit: keep the build at zero warnings.
- Chat may be German; everything committed is English.
