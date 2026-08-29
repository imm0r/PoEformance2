# Architecture

PoEformance (C# port) is a **reverse-engineering workbench for Path of Exile 2 that
also renders overlays** — not the other way around. This document is the map: read it
once and you know where everything lives and why.

The predecessor is the AHK v2 tool (same author, `imm0r/PoEformance`); the domain
knowledge — offsets, drift history, decode logic — carries over from there. The
*architecture* deliberately does not.

## The three ideas everything else follows from

### 1. Offsets are data, not code

All struct knowledge lives in **`schema/poe2.offsets.json`**: field offsets, types,
free-text drift history, and — the important part — **invariants** describing what a
correct value looks like.

```jsonc
"W2SMatrix": {
  "offset": "0x1A0", "type": "mat4x4",
  "invariant": { "kind": "unitVector3", "at": "0x30" },
  "comment": "DRIFT HISTORY: was 0x1A8 - a patch shifted the matrix -8 bytes ..."
}
```

Why this is the centrepiece: the single most expensive recurring cost of this kind of
tool is **offset drift**. Every few game patches a struct moves a few bytes and some
feature quietly starts reading garbage. The AHK tool lived through this repeatedly
(W2S matrix −8, AreaInstance +0x18, AnimationId +0x10), and each time the cycle was:
notice weird behaviour weeks later → guess → write a one-off probe script → find the
new offset → edit a constant → rebuild.

With invariants in the schema, `SchemaValidator` runs at attach time and turns the
same drift into a red row in a report, immediately, with the failing value printed.
The schema is simultaneously:

- the **reader's input** (where decoders get their offsets),
- the **validator's contract** (what correct memory looks like),
- the **workbench's display model** (the struct viewer annotates raw bytes from it),
- and **hot-reloadable** — edit JSON, press reload, no rebuild, no re-attach.

### 2. Every memory read goes through `IMemoryReader` — so sessions are recordable

`IMemoryReader` has one real method: `TryRead(address, span)`. Three implementations:

| Implementation | Purpose |
|---|---|
| `LiveMemoryReader` | ReadProcessMemory against the running game (read-only handle) |
| `RecordingMemoryReader` | wraps another reader, writes every successful read to a file |
| `ReplayMemoryReader` | replays a recorded file as if it were the live process, with frame seeking |

Consequences, in increasing order of importance:

- decoders are unit-testable against synthetic memory (`FakeMemoryReader` in tests);
- a bug report can be a **recording** — load it and see exactly what the reporter saw;
- the whole tool can be developed **without the game running**, including on CI and
  on non-Windows machines (`PoEformance.App --record x.rec` once, `--replay x.rec`
  forever after);
- time scrubbing: `Seek(frame)` answers "what did this memory hold 3 seconds before
  I died", which is the foundation for death-recap-style features.

One discipline follows: reads must behave identically live and replayed. String reads
shrink adaptively on failure instead of demanding exact block sizes, so a replay that
captured 54 bytes serves a 512-byte request gracefully.

**And one limit, which is easy to forget when a recording is being treated as evidence: a
replay only serves reads that actually happened.** A feature that was switched off while
recording leaves no trace, and walking its pointers against that file fails for want of data
rather than because the offsets are wrong — an inconclusive result that looks exactly like a
negative one. (Checked, on the session recorded 2026-08-07: the UI root's own `Self` pointer
is in there, because resolving the chain reads it, but `root+Children` never was — so nothing
in that file can say anything about the atlas.) To record something that can answer questions
about a feature offline, **that feature has to be running while the recording is made**.

**And one thing a recording has to be, to be evidence at all: long enough to contain the
moment in question.** A capture taken while clearing a map turned out to hold 437 milliseconds
— it hit its size cap before the first monster died, and every question anyone wanted to ask
of it was about what happened afterwards. The file was 25.7 MB and an ordinary archiver packed
it to 200 KB, which said plainly where the space was going: 98.5% of the 684,849 reads in it
were the same addresses carrying the same bytes as the read before. Two changes, in that
order:

- **A read that says nothing is not written.** The replay serves "the newest data at or before
  the current frame", so an unchanged read that was never recorded resolves to identical bytes
  from the frame that did record them — dropping it cannot change what a replay sees. It is
  the comparison against the last bytes *written* that makes this safe, and the round-trip
  tests are the check on the argument rather than on the code.
- **The entry stream is Brotli-compressed on the way to disk**, the header left in the clear
  so the file is still identifiable and its version still readable.

Together, on that same session: **25.7 MB → 137 KB**, with every sampled read replaying
identically.

The cost is that a killed session decodes to its last complete *block* rather than its last
complete *entry* — and that cost turned out to be much larger than "flush more often" could
fix. **`BrotliStream.Flush()` does not make the data readable**: the encoder emits nothing
until its input block fills, and that block is eight megabytes. Two real recordings ended at
exactly 8,388,608 decoded bytes each, one of them a twelve-minute map clear that had been
killed rather than closed. The same number twice, from sessions of different lengths, is a
block size and not a coincidence. `BrotliEncoder.Flush()` behaves identically; Deflate flushes
honestly at nearly twice the size. What works is finishing the Brotli stream and starting the
next one, so the body is a **chain of finished streams** rather than one long one.

Segment size is then the trade, because a boundary costs the compressor its history. Measured
on that session's entry stream (8.4 MB, 11.2 KB/s): 8 KB segments cost 46% in size for less
than a second at risk, 128 KB cost 9% for eleven seconds, 2 MB cost 1% for three minutes. The
writer closes a segment every 128 KB, or every 5 s when almost nothing is being written.
Re-recorded through it and killed without closing, that session kept **15,705 of 15,786
frames (99.5%)** in a file 17% smaller than the one that lost the last ten minutes.

### 3. Layers the compiler enforces

Six projects; references point strictly downward. Getting this wrong is a build
error, not a review comment.

```
PoEformance.App        composition root - wires everything BY HAND in Program.cs
   │
   ├── PoEformance.Overlay   ImGui in-game overlay        (net10.0-windows)
   ├── PoEformance.Config    WebView2 config window        (net10.0-windows)
   │        │
   │        ▼
   ├── PoEformance.Features  radar/loot/alert/automation LOGIC - data in, data out
   │        │
   │        ▼
   ├── PoEformance.Game      PoE2 domain: entities, player, terrain, UI tree
   │        │
   │        ▼
   └── PoEformance.Core      process attach, RPM, patterns, schema, record/replay
                             (plain net10.0 - builds and tests on any OS)
```

- **Core** knows nothing about Path of Exile. It could attach to Notepad.
- **Game** turns raw memory into typed snapshots using the schema. No UI, no features.
- **Features** consume snapshots and produce plain data ("these dots, this text, this
  alert"). They draw no pixels — that is what makes them testable against recordings.
- **Overlay / Config** render what Features computed. Windows-only, thin.
- **App** is the only project that sees everything. There is no DI container, no
  plugin loader, no reflection, no event bus: `Program.cs` constructs objects in
  order and hands them to each other. "Who calls this?" is always answerable with
  Find References.

Things deliberately absent (each one is a lesson from reading GameHelper2): plugin
system, launcher/updater, DI container, interfaces with a single implementation,
inheritance-based feature classes.

## Threading model

One **reader thread** produces immutable snapshots at its own pace; publishing a
snapshot is a single atomic reference swap. The **render thread** (ImGui) reads
whichever snapshot is current — always internally consistent, never locked. Heavy
computation (pathfinding, pricing) gets explicit worker threads that read snapshots
and publish results the same way.

No shared mutable state, no locks in the hot path, no async in the read loop. The
AHK tool needed two extra *processes*, shared memory and a seqlock protocol to
escape its single thread; here the same headroom is a `Thread` and a field.

**Implemented** as `Features/SnapshotFeed`. The renderer previously called the reader
directly, so every drawn frame walked the entity map — the frame rate was bounded by
memory reads, and worst with a screen full of monsters. Now the reader runs at 30 Hz
(entities move at the game's tick rate; reading per frame bought nothing) and the
renderer picks up whichever snapshot is newest. The atomicity is a consequence of
`WorldSnapshot` being an immutable record: publishing is one reference assignment, so
a torn read is not something the code has to prevent. The overlay shows read time next
to frame time, which is what makes a regression visible rather than felt.

## Reverse-engineering first

The workbench features are the product, in build order:

1. **Drift report** *(done — the vertical slice)*: resolve statics, walk the pointer
   chain, validate every schema invariant, print pass/fail per field.
2. **Struct viewer**: raw memory annotated live from the schema, hot-reload on edit,
   change highlighting.
3. **Watch expressions**: named, typed, saved pointer chains
   (`AreaInstance→+0x598→+0x20 as ptr`).
4. **Continuous diff**: "show me every offset that changes while I cast" — replaces
   one-off probe scripts as a class.
5. **Drift scanner**: when an invariant fails, sweep the neighbourhood for candidate
   offsets that satisfy it. One generic tool instead of a new probe per incident.
6. **Session recorder UI**: record, scrub, share.

Overlays (radar, vitals, loot) build on the same snapshots afterwards; automation
comes last.

## Deployment

Native AOT (`PublishAot=true` on App), AOT/trim analyzers on everywhere so
violations surface at build time, not at publish time.
`System.Text.Json` uses source generation for the same reason.

**Open risk, to verify early on Windows:** WebView2's COM interop under full AOT.
Fallback if it misbehaves: self-contained trimmed deployment for the Config window
process. ImGui via the native cimgui binding is unproblematic.

## Conventions

- Language: English for everything in the repo (code, comments, commits, docs).
- Comments explain *why*, and every non-obvious decision cites its reason — often a
  concrete incident from the AHK tool's history ("drift history: ...").
- `TreatWarningsAsErrors` everywhere except tests.
- A class that needs a paragraph to explain belongs in a smaller class.
- Tests run on any OS, against synthetic memory or recordings — a test that needs
  the game running is a manual checklist item, not a test.

### The interface's own appearance

A colour the tool writes *meaning* in has exactly one home: `OverlayInk`, in the Features
layer. Not in the Overlay layer beside ImGui, for the same reason `InterfaceStyle.Tinted`
is not — a palette is arithmetic about how the tool looks, nothing about it needs a render
thread to be reasoned about, and a rule that can be argued with in a test is worth more
than one that can only be argued with in a screenshot. `OverlayTheme` decides which of
ImGui's slots each ink goes into, and nothing else; a slot there names an ink or a stop on
the ink's own ramp, never a new colour.

That split is a correction. There were twenty-two private copies of these colours across
the overlay — six oranges for a warning, three greens for "good", two rarity ladders that
disagreed, and a live readout writing its quiet text in a *cold* grey where every other
window used a warm one. Nobody chose any of that; each was chosen once, beside the last
one nobody could see.

Two rules hold it together, and both are checked in `OverlayInkTests` rather than asserted
in prose:

- **Every ink is readable on what it is drawn on** — the panel *and* the band under a
  picked row. The second is where this started: at the old selection colour the game's
  unique orange sat at 2.2:1, which is not a dim name but an unreadable one, on the row
  somebody had just clicked. The band was pulled down the warm ramp until the game's own
  darkest colour cleared 3:1 on it.
- **How far apart is far enough is the game's number, not ours** — the floor for the three
  status inks is the distance between the game's unique and currency colours, the tightest
  pair a player is already expected to tell apart at a glance.

Spacing follows the same idea one step over: `InterfaceMetrics` makes every padding and gap
a ratio of the text size rather than a constant tuned by eye at 18px, since that size is
adjustable from 12 to 30. Every ratio reproduces today's pixel value at 18, so at the
default nothing moves at all.

## Building and running

```bash
dotnet build                                   # whole solution, any OS
dotnet test                                    # Core tests, any OS

# Windows, game running (elevated):
PoEformance.App                                # attach + drift report
PoEformance.App --overlay                      # the in-game overlay
PoEformance.App --record session.rec           # same, capturing everything
PoEformance.App --replay session.rec           # rerun against the capture, no game needed
PoEformance.App --record s.rec --questflags    # + read where a character's quest flags could be
PoEformance.App --record s.rec --actionhunt    # + hunt the Actor's action fields (see below)

# Look at one address somebody already found (Cheat Engine path, as written):
PoEformance.App --peek "PathOfExileSteam.exe+468C3A8,235C"
PoEformance.App --peek "+468C3A8,235C" --peekwatch   # + print the slots that move

# Or anchored on a schema static, which survives a patch where an RVA does not:
PoEformance.App --peek "GameStates,88,290,5A0,60,188,248" --peekwatch --record qf.rec
```

`--peek` takes a Cheat Engine pointer path — a module-relative base and a chain of offsets —
and says what is actually at the end of it: the walk hop by hop, the eight-byte slot the
address sits in, and every neighbouring slot with what its pointer leads to, numbered from the
object rather than from the process. `--peekwatch` then re-reads on a timer and prints only
what moved, which is how an unknown slot is identified: do the thing in the game, read the
short list.

The reason it leads with the ALIGNED slot is a trap this project walked into. A four-byte scan
found a value that was 999 while the cursor was on an inventory item and 1000 while it was not
— repeatable, and entirely an artefact: 999 is `0x3E7` and 1000 is `0x3E8`, the top halves of
a 64-bit heap pointer seen four bytes into an eight-byte slot. The pair is different every time
the game launches (the fixtures show 1054/1055, 646/647, 940/941, 1513), so an equality test
against one of them is a test against one launch.

The rest of that hunt is why the summary exists. Ten seconds of watching printed six hundred
lines and the answer was in none of them — it was in their distribution: one slot took two
values over and over, its neighbour never repeated a value at all because it was a clock, and
the pair before them were `_Ptr`/`_Rep` of a `std::shared_ptr` (always exactly `0x10` apart,
which is `make_shared` putting the payload inline behind the control block). So a slot that
keeps moving now goes quiet after a few lines, everything is tallied, and the tally prints at
the end. `tests/fixtures/session-2026-08-hover.rec` is that session, and `HoverSlotTests`
replays it. See the block comment at the top of `schema/poe2.offsets.json`.

`--questflags` was the hunt that did not find it. It locates the QuestFlags table through any
NPC in the area and sweeps ServerData and the state objects for references to its rows — and
that sweep could never have worked, because the set stores no references at all. It is kept for
the READING: the regions land in the recording, so a question that needs the game becomes one
that can be answered offline as often as it takes, which is how the chain was confirmed once
somebody handed it over. See "Quests" below for what it actually is.

`--actionhunt` **found the action fields**, and it now also reads them back. It samples the
player's Actor *and every hostile monster's* while the person plays a small protocol (click-move
and ARRIVE, then a few casts; for the monster half, stand in a fight and let things walk at you),
scores candidates by what the game then does, and finishes with two verdicts: whether the
monsters bear the same offsets out, and a readout of what the schema's action fields say right
now.

What that hunt settled, from `tests/fixtures/session-2026-08-actions.rec`:

| Field | Offset | What it is |
| --- | --- | --- |
| `Actor.ActionId` | 0x2A0 (short) | 0 nothing, 2 skill, 4224 move |
| `Actor.SkillActionPtr` | 0x238 | → `ActionWrapper` while casting |
| `Actor.MoveActionPtr` | 0x240 | → `ActionWrapper` while moving |
| `Actor.CurrentSkillPtr` | 0x3A0 | the skill being cast, 1:1 with the animation id |
| `ActionWrapper.TargetGrid` | 0x150 | where it is aimed — **integer grid cells** |
| `ActionWrapper.OriginGrid` | 0x180 | where it started from, same units |

The destination is proven the way this project prefers: not structurally, but by the game. Over
the four completed move actions in that session the player came to rest at exactly
`TargetGrid + (0.5, 0.5)` cells — every axis of every arrival inside 0.4999..0.5000 — so the
stored integer names a cell, the actor stops in the middle of it, and `ActionReader` converts
with that half cell to predict the arrival to **0.00 world units**. `ActionFieldsTests` replays
the fixture and asserts it.

Two things that recording taught beyond the offsets, both of which matter more for a warning
system than the offsets do. **An action can run with no animation at all**: two frames carry a
committed skill action with a real target while `AnimationId` reads Idle, so an animation-only
reader is blind to exactly the earliest moment. And **the skill pointer follows the cast, not
the commitment** — it is still null in those frames, so the earliest signal available is
`ActionId` plus the wrapper's target.

A **second session** (`tests/fixtures/session-2026-08-fight.rec`, a different area on a
different day) re-derives all of it from scratch — the same pointer slot, the same short id, the
same pair at the same offset, with 43 arrivals where the first had four, and a scale-free fit
that recovers the grid factor without being told it. Two independent sessions agreeing is what
separates a measurement from a coincidence, and `FightSessionTests` asserts it.

That second recording also taught two lessons about the *tool*, both of which cost it the
question it was made for, and both fixed:

- **It sampled only the player**, so the entity list in the file is whatever the startup scan
  saw — seven entities, frozen, none of them a monster, through 86 seconds in which the player
  demonstrably fought. A recording holds only what the running build read, and no build had ever
  read a monster's actor. The hunt now walks the entity map every tick with `ReadActions` and
  `ReadAim` on, so the monsters land in the file.
- **A failed resolution was cached.** The player pointer is one address all session, so a single
  unreadable frame at the start — a loading screen — stuck, and every later frame returned
  nothing. Replayed against the build that made it, that file yields *zero* samples out of 1390
  readable frames, and the report says "no frames" about a perfectly good session. Nothing
  crashed and nothing warned; it is the expensive kind of bug, and it is now a regression test.

**The monster question is settled** (`tests/fixtures/session-2026-08-monsters.rec`: 130 seconds
of a real fight, 54 monsters, 27,156 sightings, 9,300 of them acting). Every offset above had
been measured on the *player's* actor while the feature they exist for reads *monsters*, and the
game answered in the two ways it can:

- **The arrival.** 210 monster moves ran to completion and ended on the destination the field
  named — **185 of them exactly**: median miss 0.00 world units, worst 10.87, which is one grid
  cell. Across 39 distinct monsters of eleven kinds, none of which anybody had aimed a probe at.
  A wrong offset does not pass this once, let alone 210 times.
- **The bearing.** Over 1649 monster *skill* actions the direction from origin to target agrees
  with `Render.RotationCurrent` to a median of **1.6°**, 94% inside thirty — a field found a
  month earlier by a different method on a different recording, so this is two unrelated readings
  agreeing rather than one looking plausible.

Two things that session added. `ActionId` is a **flags word**, not an enum: seven values turn up
and they decompose into two bits — every id carrying `0x0002` had the skill slot filled, every id
carrying `0x1000` had the move slot filled, and `0x1002` had both. Read as whole numbers five of
those seven are strangers; read as bits they are two facts, which is what `ActionReader` now
tests. And **monsters do not face where they walk** — a move's bearing sits 25.9° off the facing,
and measuring it from where the monster currently stands makes it *worse* (32.5°). It faces its
quarry and walks around obstacles, exactly as the player faces the cursor rather than the path.
That is why `MonsterActionCheck` reports the two bearings apart: mixed, they drag a corroborating
1.6° out to a doubtful 17.7° and slander a working field.

`WorldReader.ReadActions` remains a separate switch from `ReadAim` — now not because the actions
are unproven, but because they cost four reads per entity where the aim costs two, and a layer
that only wants a direction should not pay for a destination. Offsets go in the schema, not in
code; see the `Actor` and `ActionWrapper` block comments in `schema/poe2.offsets.json`.

## Status

**The vertical slice is complete** (confirmed in-game, 2026-08): attach, pattern-scanned
statics, the pointer chain to the player, the entity map, components, both projections, and
entity dots on screen — placing markers exactly where the author's production AHK tool does.

What that exercised, and what is therefore trustworthy: the pattern scanner, the drift
report and its invariants, record/replay, the component lookup, the world-to-screen matrix
at `WorldData+0x1A0`, the UI element tree with its parent-chain accumulation and two-axis
scaling, and the map's own isometric transform.

### What the slice cost, and what it taught

Four findings were only visible in real memory, and each is written down where it will be
found again — the schema for offsets, `CLAUDE.md` for the working rules:

- `LocalPlayerStruct` is inline, not a pointer.
- `ComponentLookupEntry.Index` is 32-bit; reading 8 bytes merged it with the next field and
  dropped most of every entity's components.
- The camera matrix moved, and the invariant meant to catch that **rejected the correct
  offset and accepted a decoy** — a block of unit-length rows that satisfied the byte
  pattern perfectly while collapsing the whole scene onto one pixel.
- The check meant to catch *that* — "does the player land at screen centre?" — passes
  trivially for any matrix that inflates `w`, because everything lands at the centre.

The lesson generalises past this project: **a check a wrong value passes is worse than no
check**, and structural fingerprints (a unit vector, a plausible pointer) are weak evidence.
The tests that hold now are ones the game itself settles — project an entity's health-bar
height and see whether it lands on the bar the game drew; require the scene to spread, not
just the player to centre.

### Next

Two of the three follow-ups are now closed:

- **WebView2 + Native AOT — RESOLVED.** The official binding is built-in COM and cannot
  AOT; `smourier/WebView2Aot` (source-generated COM, no UI framework) can, and the
  official package is kept only for its native loader DLL. CI publishes the whole graph
  with `PublishAot=true` on every push, and an AOT-built window was confirmed running
  in-game. The bridge is JSON over web messages with source-generated serialisation —
  reflection-based JSON is exactly what dies silently under AOT.
- **Threading — RESOLVED.** See the threading model above.

**Auto-flask — the first feature on the slice**, and a useful record of what "read a
value and press a key" actually involves. The threshold is the easy part; what took the
work was everything around it:

- the percentage is measured against the UNRESERVED pool, not the maximum. With half of
  mana reserved by auras a full globe reads 50% against the max, so any threshold below
  that fires forever.
- flasks are found by CONTENT, not by inventory id — the id moves between patches, "the
  inventory full of flasks" does not.
- the item list holds one entry per occupied grid cell, so items must be deduped by
  entity pointer or a two-cell flask appears twice.
- charms share the belt and the `Flasks/` path but the game triggers them itself, so
  they are belt contents without being pressable.
- an active flask buff carries its belt SLOT, which answers "is this flask still doing
  its job" as fact where a cooldown only guesses.
- input is gated on the game having focus, in the DECISION rather than in the code that
  presses keys, so no future caller can bypass it.

### Features on the slice, and what each one turned out to be about

Every one of these reads a finished `WorldSnapshot` and touches no memory, which is why they
are testable against a recording and why none of them can slow a read down. In each case the
interesting part was not the feature:

- **Evasion: warning and acting, and why they are two settings.** The action fields say what a
  monster has committed to and *where that lands*, so this marks the place and can roll you out
  of it. `EvasionPlanner` is pure in the way `AutoFlask` is — it returns *whether* to dodge, and
  `Program.cs` does the pressing at the one place in the codebase that can synthesise input —
  which is what lets every gate between a monster twitching and a keystroke leaving the process
  be a unit test. Drawing and acting have **separate rarity floors** because they cost different
  things: a marker for a white monster is a ring on the screen, a keystroke for one is a roll
  charge, and white monsters are most of what an area contains. Both default to off.
  - **What decides a roll's direction**, settled by the owner testing it under WASD: a held
    movement key wins, and with none held the roll goes towards the cursor. Left alone the tool
    supplies only the **timing**, the half a person cannot do: an attack is committed, and its
    landing spot known, before any animation shows it. Two minutes in front of a map boss, the
    owner steering and the tool pressing, cost zero hits — which is why that stayed a mode rather
    than becoming a stepping stone.
  - **Steering, and why the danger had to become a line.** Timing alone loses one case, and the
    owner named it: a boss channelling a beam *at* you, while you point at the boss, because that
    is what you do when you are fighting one. So the roll runs down the beam. Switching `Steer`
    on has the tool hold a movement key for the length of the roll — the rule above says that
    wins over the cursor, so no mouse is touched. The scoring is the part worth reading
    (`Escape`): **the threat is a segment from where the action starts to where it is aimed,
    extended past the target by one roll.** Scoring by distance from the target *point* instead
    rates rolling backwards exactly as well as rolling sideways — both end a roll's length from
    the same point — and backwards stays in the beam for its whole length. The extension is free
    for everything else: nothing here can tell a beam from a slam (that needs the game's skill
    data, which this tool does not read), and for a slam centred on the target sideways is just
    as safe as backwards anyway. A rule that is right for one shape and costless for the other is
    the one to take when the shape is unknown. Each of the eight key directions is scored by its
    **worst** threat, not its average — escaping one attack into another is not an escape — and
    every action with a target counts, including ones the rarity gates would never have drawn:
    what to draw, what to react to, and where it is safe to land are three questions. If nothing
    beats standing still it does not roll, and says so.
  - **Which way is "W"** comes from the game's own matrix, not from an isometric constant:
    project the player, step up the screen, invert onto the player's ground plane, and the
    difference is the world direction (`ScreenBasis`). Over the 1984 in-game frames of the
    monsters fixture that gives up-screen = world (0.7071, 0.7071) and right = (0.7071, −0.7071)
    — the world axes run diagonally across the screen — with the two screen axes coming back
    **0.19° from perpendicular in the world** at worst, which was not guaranteed and is what
    makes the diagonals evenly spread. The decisive test puts the derived direction back through
    the projection and asks whether it lands directly above the player on screen; a sign error, a
    swapped column or a row-major reading all fail it, and none of them fails a length check.
    The one thing *not* measured is that the game's forward key moves the character up the
    screen — nothing in a recording answers that — which is why steering ships off.
  - **Giving the keys back is the delicate half, and it needs a keyboard hook.** Windows has one
    up/down state per key: a synthesised W-up is not "the tool's W-up", it is W being up, and the
    player's finger on the physical key does not put it back. So the sequence is release, steer,
    roll, restore — and after the tool's own key-up, *no* API can say whether the player is still
    holding W. Restoring the snapshot blind fails when they let go mid-roll: their release lands
    on a key that is already up, the restore presses it down, and the character runs forwards
    until they happen to tap it again. `PhysicalKeys` is a `WH_KEYBOARD_LL` hook that ignores
    `LLKHF_INJECTED` events, which is how the AHK tool backs its own `GetKeyState(key, "P")`. It
    is installed on the first tick steering is switched on, never at launch, on its own thread
    because a low-level hook is only delivered to a thread that pumps messages — and one that
    does not pump installs successfully and then silently never fires. `BlockInput` is not the
    answer to this: it does not clear what is already held, and it swallows the release, which
    produces exactly the stuck key it was meant to avoid.
  - **Do not read `RotationCurrent` as the roll's direction.** It follows the cursor, so it is
    right only for the no-key case; on a key-steered roll it points elsewhere, and on a backward
    one exactly the other way. Two wrong explanations came out of those correct numbers before
    the rule did: first that a roll can only run along the line already faced (so sideways was
    impossible and arming the dodge was "a coin toss"), then that the facing locks onto a target
    — the second at least checked against the fixture and refuted by it, the nearest monster
    being 1100 units away and up to 124° off. Worth keeping as the shape of the mistake: a number
    was asked what it meant instead of the person who could see the screen.
  - **The dodge key is the one key not read from the game.** The flask spellings were
    established against a real config; nobody has established what this game calls the dodge
    roll, and reading a plausible-looking line would be a guess dressed as a measurement — one
    that presses a key the player never bound. `DodgeKeyHints` shows the candidate lines from
    the ini and picks none of them. The AHK tool settles it the same way.
  - **Naming the skill: what is known, and what the next recording has to answer.** The warning
    knows an attack is committed and *where* it lands, never *what* it is — which is why every
    threat is one shape. The route to a name is `Actor.CurrentSkillPtr`, and four things about it
    are now measured rather than assumed (`SkillObjectTests`, against the monsters fixture):
    - **It is finer than the skill, correcting this project's own claim.** The schema recorded a
      1:1 correspondence with the animation id from 27 frames of one session; over the monster
      session it is **four objects to three animation ids**, two of them both playing 299. Each
      object plays one animation, but not the reverse — so the pointer keys "same cast as last
      frame", never "which skill is this".
    - **The action wrapper does not carry it** anywhere in the 0x200 anyone has recorded. PoE1
      put the skill at wrapper+0x150; in PoE2 that is `TargetGrid`, so the obvious port lands on
      a field that reads as plausible integers.
    - **Only 53 of 122 committed skill actions had a skill object at all** — the timing problem
      as a number. Naming from this pointer cannot be the whole answer for a warning meant to
      fire before the cast is under way, which is why the hunt also walks the actor's own
      granted-skill table.
    - **Every pointer that leaves the object is a dead end offline.** Its own 0x200 is in the
      file; nothing follows the five outward pointers (0x000, 0x008, 0x010, 0x1F0, 0x1F8). A
      recording holds only what the running build read, so this needs a new session — which is
      what `--skillhunt` is for.

    The hunt searches for **text**, because that is the shape of the answer: `ActiveSkills` and
    `GrantedEffects` both carry `Id: string` as their first column, and this codebase already
    resolves two other dat rows exactly that way (`ItemReader` — "the dat row's first field is a
    pointer to the mod's id string"). It follows two hops out and reports every string with the
    offsets that reached it, marking only the chains that gave a **different** name to every
    skill — because a class name or an engine label gives the same one to all of them and looks
    like an answer until it is asked to tell two skills apart. It hunts the broken
    `ActiveSkillDetails.CastType` in the same pass, by scanning each entry for the *live*
    animation id rather than trusting a reference's offset.

    What the game's data will **not** give, checked against dat-schema's `poe2/_Core.gql`: no
    radius, area or shape column exists on `ActiveSkills`, `GrantedEffects`,
    `GrantedEffectsPerLevel` or the stat sets. Radius, where it exists at all, is a stat row
    reached through `ConstantStats`/`AdditionalStats`; **shape is nowhere**. So a per-skill table
    curated by hand, keyed on the id this hunt is after, is the realistic route to anything
    better than the line model — the way `data/animations.tsv` already works.
  - **A monster's move bearing is not a check on anything**, which the same recording measured:
    monsters face their quarry and walk around obstacles, so a destination 26° off the facing is
    the game working. Only aimed *skills* corroborate the facing, and the report says so.
  - The one bug the tests caught before the game did was mine: `long.MinValue` as the "never
    dodged" sentinel makes `now - last` **overflow**, so the first threat of every session reads
    as still cooling down and nothing is ever pressed. It looks exactly like a working tool.

- **Read cost over time.** A live number answers "is it slow now", which you can already see.
  The useful questions need the shape over a whole map, per phase — one graph for the total
  says a frame was expensive and nothing about why.
- **Damage over time, stacked by how much of it is known.** The same argument one step on: a
  live dps figure answers "how am I doing right now", and the questions worth asking need the
  shape. Two things decide whether such a graph is worth having. It plots a RATE, taken as the
  difference of the meter's running totals over a fixed quarter-second — a total over a map only
  goes up, and the shape of a ramp says nothing; taking differences of the meter's own numbers
  also means the graph and the readout cannot disagree, whatever the crediting rules decide. And
  it is STACKED in three colours rather than drawn as one line, because on a build that one-shots
  packs the majority of the figure is inferred rather than watched: a burst made of the assumed
  band is a different event from the same burst watched off monsters' health, and one line cannot
  tell them apart. `PlotLines` takes one series in one colour and is therefore exactly the graph
  this must not be, so it is drawn by hand — which also buys the hover readout, and each bar
  carries a **census of what was around** when it happened, by rarity. That is what makes the
  number mean anything: five thousand into a rare is a build working, and five thousand into
  forty white monsters is a build that cannot single-target. Counted through the meter's own
  monster filter, so the census describes the monsters the figure is about; in the game's own
  rarity colours, which nobody has to learn. Not the boss flag — it sits in the schema marked as
  an unverified hypothesis, and a readout is not what an unverified offset gets built on.
- **The figures a build is actually compared by, and why the obvious one is not among them.**
  `Peak` was the only burst number and it is the one figure here that cannot be compared with
  anybody else's: it is the high-water mark of a smoothed average, so it under-reports the real
  burst and *moves when the smoothing slider does*. Asked the other way round — "over any window
  this long, what is the most that actually landed" — it needs no smoothing, has nothing to
  configure, and means the same thing on every machine. Three lengths, because a second (the
  opening hit), five (a rare going down) and ten (whether it can keep going) are different
  questions a build can pass and fail separately. Windows never span a hole, which is why each
  sample carries its own span rather than having it inferred from the gap to the one before.
- **Two things that fell out of data already being kept.** Single-target dps is not a separate
  measurement: every bar already records how many monsters were near, so the stretches with one
  monster near *are* the single-target fight — no dummy-hitting exercise needed. And the kill log
  answers "how long does a rare take", which a rate averaged over a map cannot, because no map is
  one long fight. Its clock starts at the first damage rather than the first sighting, and a kill
  the tool does not believe in — refused by the distance gate — never appears at all, because a
  log of kills containing ones it doubts is not a log of kills.
- **Seeing the effects, which the read throws away three times over.** A debugging layer, off by
  default. The interesting part is what it says about the name: **the game's actual particles are
  not entities** — nothing in memory lists a spark. What is listed is the effect ENTITIES: ground
  effects carrying Life and a position, and the engine's `/fx/` asset nodes. A screen of fire is
  one entity, not a thousand, and saying so is the difference between a working tool and somebody
  concluding the read is broken because the spark they looked for was never there. Turning it on
  undoes three separate decisions that are each right for playing — the noise filter refusing
  engine nodes before their components are read, the reader dropping hostile effects, and the
  overlay declining to draw friendly ones. The safeguard that matters: a kept effect is
  **reclassified**, not un-dropped. Letting it travel on as a Monster would put it back into every
  count and every health bar the dropping exists to protect — it carries Life, which is exactly
  why a Firewall build once covered its own screen in enemy markers.
- **A heat map, because the map was already being drawn.** The cheapest interesting thing that
  could go on it: the meter already knew how much happened and the snapshot already knew where
  the player was, and nothing was writing the two down together. A total says a map was hard; a
  picture says WHERE, and only the second is worth acting on. Three things worth recording about
  it. It is anchored at the PLAYER for all three measurements, which is a choice and not the
  only one — damage dealt could be filed where the monster stood, and for a question about pack
  density that would be better; one rule for all three is what keeps them comparable on the same
  picture. The scale is the area's **95th** busiest patch rather than its busiest, or one boss
  standing in one place flattens the rest of the map into the same shade of nothing. And it is
  OFF by default: a picture of a whole map is for afterwards, and painted always it would be a
  wash of colour under every marker for the whole of every map.

  The first version was sparse and unreadable, and the two causes are both worth keeping. It
  looked SPARSE because a sample was filed at the point of the read, and reads land thirty times
  a second while a running player crosses several patches between two of them — so the picture
  was a dotted line through ground that had been walked straight across. The fix is to lay each
  step ALONG the segment from the last position, sharing the amounts across the patches crossed
  (with a jump guard, or a portal paints a stripe across the map). And it was UNIDENTIFIABLE
  because a single colour faded to transparent produces orange-ish squares, which is precisely
  what the loot and monster markers are: the reporter could not tell their own heat map from the
  monsters on it. A real five-stop ramp fixes that, and the low end being blue-green is what does
  the work — no marker in the overlay is either colour, so a blue-green wash is unmistakably
  ground rather than a thing standing on it. Plus a key in the map's corner, because the first
  question anybody asks of a coloured map is which of the two it is.
- **A death is not a hit.** Damage taken is the same pool-difference measurement pointed at the
  player, and at zero life the pool reads as *unread* rather than empty — otherwise the whole
  pool is counted as one enormous hit on the way back in, and every death becomes the worst hit
  of the map. The same baseline mistake (take it at the area's FIRST reading, not the second) has
  now been made twice in this file and caught by tests both times.
- **Complete overlay configurability.** A catalogue of every drawn thing, with the editor
  GENERATED from it, because a hand-written settings page is how a tool ends up with fourteen
  configurable things and three that are not. The promise it makes — everything painted over
  the game is in the catalogue — is only true if adding to the overlay is not finished until
  it has an entry, so the catalogue is the boundary and the boundary is written down.
- **Alerts.** The failure mode is SPAM, not a missed alert: a radar that interrupts constantly
  gets switched off within a map, at which point it also misses everything. Once per entity,
  one banner per moment, a gap between banners, nothing in town — and identity is the game's
  entity id rather than its address, because addresses get recycled inside an area and a
  recycled one reads as "already mentioned".
- **Map coverage.** The denominator is the whole difficulty. The walkable grid holds a great
  deal of ground nobody can reach, so the figure is measured against a flood fill from where
  the player came in; against every walkable cell a finished map reads as a few per cent.
- **Keeping what walked out of range.** The entity list is a BUBBLE around the player, not an
  area, so everything the tool worked out about a strongbox left for later leaves with it —
  which is exactly when it becomes worth marking. Over the recorded map the game listed 143
  standing things at one point or another and 12 of them at the end. `EntityMemory` keeps the
  rest. Two things make it safe rather than a pile of stale markers. Only things that DO NOT
  MOVE are kept — places and floor drops, never a monster, because a remembered monster dot is
  wrong the moment it is drawn. And a sighting is dropped when the thing vanishes while the
  player is close enough to have seen it go, which is the reference's own rule
  (`CanExplodeOrRemovedFromGame && DistanceFrom(player) < NETWORK_BUBBLE_RADIUS`). The
  threshold was checked against the recording rather than taken on trust: every drop-out from
  the entity list is either under 26 cells (consumed underfoot) or over 165 (out of range),
  with nothing in between, so the reference's 150 sits in an empty band and 130 sightings were
  dropped over that map without one of them being a mistake that outlived a single frame. The
  same walk settled the key: 200 things came back into the game's list after being remembered
  and 98 of them came back at a NEW ADDRESS, so the memory is keyed on the entity id.

- **Health bars, and looted chests.** Both came out of reads that were already happening or
  already described: the corpse check read a monster's current health and threw the maximum
  away, and the Chest offsets sat in the schema unread. The interesting part is how they FAIL
  - an absent component is not "dead" and not "opened", and getting that backwards makes a
  whole feature disappear the moment an offset drifts, with no error to trace.
- **Routes that stay on one storey.** The walkable grid is flat: a bridge and the ground under
  it are the same cells and both walkable, so a two-dimensional search draws a route through
  the floor. Only the height separates them.
- **Pinning a window down, or handing it to the mouse.** Two things per window: LOCKED (still
  clickable, cannot be dragged out of place by a stray click) and CLICK-THROUGH (the mouse does
  not see it at all). The second looks like it should need Win32 work and needs one ImGui flag:
  ClickableTransparentOverlay flips the whole overlay window between clickable and
  `WS_EX_TRANSPARENT` every frame on `io.WantCaptureMouse` alone, and a window carrying
  `NoMouseInputs` never sets it — so trying to do this with our own hit-test regions would be
  fighting the library for the same setting. The part that needed thought was the way back: a
  click-through window cannot be right-clicked, so its own menu is gone the moment it is
  switched on. That is now the pointer ICON's job — it asks for `io.WantCaptureMouse` back for
  exactly as long as the cursor sits inside that one square, so the window hands itself back
  where the switch lives. It works because the same library reads the cursor with `GetCursorPos`
  rather than from window messages, so the overlay still knows where the mouse is while it is
  transparent to it; a message-driven position would freeze on the way out and the icon would be
  unreachable with no symptom but a dead square. Two consequences worth knowing: the button
  PRESS still comes from messages, so it only arrives a frame or two after the cursor lands
  (hence acting on press, not release), and a click-through window is never collapsed, because a
  collapsed one has no title bar drawn to hold the icon. This replaced an exemption — the status
  window used to refuse click-through outright, since the only undo was Tools → Appearance, which
  is opened from a checkbox inside the status window. A switch that undoes itself where it sits
  needs no carve-out, so every window may now go click-through. The preload panel is the one that
  still leans on the list, having no title bar at all. Both switches are also two drawn icons in each window's own title bar, left
  of the close button, which is where they are visible at all — a right-click menu says nothing
  about whether a window is already pinned. Putting ImGui items in a title bar has one trap worth
  the sentence: an item is measured as window CONTENT wherever it was submitted, so a strip
  anchored to an `AlwaysAutoResize` window's right edge feeds its own width back in and the
  window walks off the screen. Two things keep it still — the strip ends at `WindowPadding`,
  exactly where the content already stopped, and the hit areas exist only while the pointer is
  in the title bar. The icons themselves are painted every frame regardless, because they are the
  state as much as the switch.
- **Getting out of the way of the game's own panels.** Everything drawn in world space is drawn
  UNDERNEATH a stash, a passive tree or a world map the moment one opens — right information in
  the way, which is worse than none. `PanelReader` reports which screen-filling panels are open
  and the world block is gated on it. Two things made this more than an `if`. The panels come
  from two kinds of place and they fail differently: the left panel, the right panel, the world
  map and the skill tree are POINTERS the game nulls when nothing is open, so a wrong offset
  reads as a bad pointer and simply never fires; the rest are CHILD PATHS, and a wrong one can
  land on a real element that is always showing — which hides the overlay forever with nothing
  to say why. So everything unreadable reads as SHUT (the safe direction: an overlay drawn over
  a panel is the status quo, an overlay that vanished is a bug report nobody can act on), the
  answer is a flags enum rather than a bool so the status window can name the panel that is
  stuck, and there is a switch to stop asking.

### Rules — the one feature whose configuration is a LANGUAGE

Ported from GameHelper2's **RuleCraft**: conditions over live game state driving overlay text,
sounds and synthesised input. The AHK tool had the same idea in `CustomHotkeys` — a boolean
condition tree per macro and an ordered list of actions — and where the two references disagree
this follows the AHK one, because it is the design that survived contact with the game.

The engine is a `RuleState` (facts gathered once per read), a `RuleCondition` tree, and a
`RuleEngine` that returns what SHOULD happen. It reads no memory and presses nothing, on the
same split as auto-flask, so the priorities, the cooldowns and every gate are ordinary tests.

- **The expression library could not come along.** RuleCraft compiles its conditions with
  `System.Linq.Dynamic.Core`, which builds an expression tree and calls `Compile()` — runtime
  code generation, which Native AOT does not have. So the grammar is parsed by hand, and it is
  deliberately a SUBSET: no arithmetic. `HealthPercent * 2 < Mana` parses there and cannot be
  drawn in a node graph, so a rule written that way would open in the editor and silently lose
  itself.
- **The TREE is what is stored.** RuleCraft stores a condition string and regenerates it from
  its graph on every edit, so a rule somebody adjusted as text loses that adjustment the moment
  a box is dragged. Here an expression and a graph are both conversions of one tree, and the
  graph carries only the LAYOUT. Neither view can overwrite the other with a stale copy.
- **A number that could not be read is null, and satisfies no comparison.** This is the single
  most load-bearing decision in the port. RuleCraft reports an unreadable life pool as 0 and
  "no rare monster anywhere" as 9999, so `LifePercent <= 35` fires on a loading screen and
  `NearestRare >= 100` is satisfied by an empty room — and both read as the feature working. It
  is also why negation is never folded into the operator when a condition is written back out:
  `!(x <= 45)` and `x > 45` differ exactly where the number is unknown, so rewriting one as the
  other would change what a saved rule does.
- **Input is gated in the DECISION.** Being in the game, having focus, and no panel being open
  are checked in the engine, not by whoever sends the key, so no future caller can reach the
  sending path around them. RuleCraft checks focus in three of its four input paths and not in
  the fourth — its plain key press — so a KeyPress rule types into whatever window the player
  alt-tabbed to, while its own documentation says input only runs while the game is in front.
- **Everything is keyed on a rule's ID**, not its name. RuleCraft keys cooldowns and interval
  timers on the name, which its own add button leaves as "New rule" for every rule somebody
  adds — so two of them share one cooldown, and renaming one hands it a fresh one mid-fight.
- **The key an effect presses can be a belt SLOT**, resolved live from the game's own config,
  which is the AHK tool's output binding rather than RuleCraft's stored letter. The difference
  shows up the first time somebody rebinds a flask: the letter goes on pressing what used to be
  flask 2, with no symptom beyond "nothing happens". `FlaskCharges(slot)` is likewise answered
  per slot, which RuleCraft's own documentation says it cannot do.
- **Two of its conditions are deliberately absent.** `HasDebuff`/`DebuffTimeLeft` call the same
  code as their buff counterparts there — the game keeps one list — so shipping them would
  advertise a distinction the tool cannot make. `Stat`/`HasStat` are gone because nothing here
  reads the player's stat block yet, and a condition that always answers zero is worse than one
  that is not offered.
- **The editor is GENERATED from the engine's own tables.** What conditions exist, which keys
  can be pressed and which comparisons there are all travel to the page as a catalogue, on the
  same argument as the overlay style editor: a hand-written list is how a tool ends up with
  thirty-four facts the engine can evaluate and twenty-nine the page can offer. The page never
  parses a condition itself either — it asks the host — so there is one parser and it is the
  one that runs.
- **What the evaluation costs is bounded by where it runs.** Once per read on the reader
  thread, not per frame: RuleCraft evaluates inside its draw callback, which makes how often a
  macro fires a function of the frame rate — the same rule types twice as fast on a better
  graphics card. The sound is the opposite case and goes OFF that thread, because
  `Console.Beep` blocks for its whole duration and a 120 ms cue played inline would stall four
  reads.
- **A caption lingers.** RuleCraft has no equivalent, which is why all of its own examples hang
  off conditions that stay true for a while: a rule fired by an interval or by a single event
  is otherwise drawn for one frame and, in practice, never seen.
- **Counting what you are AIMING at, not what is near you.** Ported from the AHK tool's cursor
  radius, which the reference plugin has no equivalent for at all. For anything placed where
  the pointer is — a wall, a ground effect, a targeted blast — "three monsters near me" is the
  wrong question: the pack behind the character does not make a wall in front of it worth
  casting.

  It shipped measuring SCREEN PIXELS from the cursor, and that was wrong twice over. **A
  circle on the screen is an ellipse on the ground**, stretched away from the camera by the
  tilt — so a pixel radius counts monsters in a region no skill has; and the number moves with
  the resolution and the zoom besides. The AHK tool has both modes and its world-space one is
  the one worth having. So the cursor is now run BACKWARDS through the camera matrix onto the
  plane at the player's height (`WorldToScreen.OnGround`) and the radius is world units, the
  same as every other radius here. The AHK tool inverts a fitted isometric constant instead —
  a scale and sin(38.7°) — which works because its ring is drawn with the same constant; the
  matrix is the game's own answer and there is nothing to fit. Its limit is worth knowing: the
  plane is at the player's height, so on a ledge or a staircase the point lands where that
  plane is rather than where the floor is.

  The inverse is pinned by a ROUND TRIP through a tilted, off-axis matrix — project a point,
  un-project it, get the point back. An identity matrix passes a projection that has swapped
  its columns or dropped its perspective divide; that one does not.
- **The ranges can be drawn on the ground.** The same working rule this project applies to
  itself: a radius is a number in a text field, and the honest way to know whether 30 is right
  is to see the circle with the monsters it counts inside it. A player ring is a world circle
  projected point by point (an ellipse on screen, and drawing it as one would need the
  camera's tilt, which the drawing layer has no business knowing); a cursor ring is the same
  thing centred where the pointer aims. Both come out as ellipses on screen because the
  projection puts them there — which is the shape the measurement actually has, and seeing
  that is what settled the pixel question above. Each carries
  what it currently reads and what it needs, and turns colour on the leaf's answer INCLUDING
  its negation, so a "no monsters within 30" ring is green when the circle is empty.

#### The two names a buff has

A rule matches the **engine identifier** — `fire_wall` — where the game paints **Flame Wall**.
Some ids are close enough to guess and plenty are not, so a buff condition was written by
guesswork, and the reference plugin's answer to that is its own debug window and a lot of
scrolling.

Both are now read and shown together, and matching stays on the ID deliberately: it is the same
on every client, where matching a display name would break every rule the moment somebody
changed their game's language. `BuffWatch` REMEMBERS what a character has had on rather than
listing only what is on now, because a buff worth a rule lasts a few seconds and is long gone
by the time anyone has switched to the config window.

The readable name is `BuffDefinitions.Name` at **0x12**, and how much that offset is worth is
the interesting part: it is **computed from the column layout, not observed in a live game**.
The same arithmetic over dat-schema's columns (string 8, bool 1, i32 4, enumrow 4, foreignrow
16, array 16) reproduces `BuffVisualsKey` at 0x55 and `BuffCategory` at 0x67 EXACTLY — and both
of those were already in the schema, derived the same way and committed to long before. Two
independent hits on a derivation are what make the third one evidence rather than a guess. The
description at 0x08 is read for the same reason it is shown: two independent strings landing
correctly at once is unlikely by accident, so a wrong offset reads as obvious rubbish in the
picker rather than as a quiet lie.

One caveat carried from dat-schema: it marks that column NOT localized, so it is a design-time
English name, and the string the game actually paints may instead be `BuffVisuals.BuffName`,
which IS localized. Which is the second reason rules match the id.

#### Three defects the first version shipped with, and what each one was

All three were reported from one session, and none of them was visible from the tests:

- **The canvas was a drawing.** `RuleGraph.ToCondition()` existed and was called by nothing, so
  a rule built entirely in the node editor kept the empty condition it was created with — which
  says nothing, and therefore fires nothing. Every part of it looked right: the graph saved,
  the wires came back, the effect was configured, the status line said it was watching. The
  fix makes the graph the SOURCE when a rule has one, derived in `Rule.Normalised()` — the one
  place a rule becomes what the engine runs.
- **The wires were drawn into the wrong box — twice, for two different reasons.** The SVG layer
  was stretched to the scrolling surface while its `viewBox` described the whole canvas, so an
  SVG asked to fit a smaller box than its viewBox squashed it; it now sits in a content element
  sized to what the boxes reach. And separately, `port()` measured a box that HAD NOT BEEN LAID
  OUT: the detail pane is built detached and appended afterwards, so every box reports
  `offsetWidth` **0** at the moment `render()` runs — and zero is a number, so the
  `?? 200` fallback beside it never fired. Every wire went to the box's top-left CORNER. Now
  the fallback tests for falsy rather than null, and a `ResizeObserver` redraws once the sizes
  are real, which also covers a box growing when its fact changes.

  Fixing the first and claiming both were fixed is the part worth recording. The A/B check
  said 1 pixel, and it was measuring a canvas that a DRAG had already corrected — dragging
  redraws with the boxes attached. **A check that passes for the wrong reason is the failure
  mode this file keeps describing**, and the fix was to measure the state actually complained
  about: the wires as they FIRST appear, no click, no drag, no re-render.
- **A slow drag snapped back.** The page polls the host once a second and rebuilds the editor
  from the answer; a drag is one edit that takes several seconds and claimed "in use" only at
  the END, so the poll landed mid-drag and restored the position from before it. Only quick
  flicks moved a box.

The last two are worth a note about how they were FOUND, because the first attempt to catch
them proved nothing. Driving the page in a browser exercises its no-host preview path, which
renders once and never polls — and the poll is the entire mechanism. A harness that installs a
fake `window.chrome.webview` before the modules load, and answers like the host does, tells the
merged build and the fix apart on both counts. A check a wrong value passes is worse than no
check, and a browser check with no host was exactly that.

#### Two more, from the buff picker — and the older bug it uncovered

The picker shipped listing one buff, named `undefined`, permanently off. Two independent
defects stacked, and only the second was new code:

- **The buff vector holds POINTERS, and had been read as inline structs since long before the
  rules feature existed.** The schema called that "a deliberate divergence from GameHelper2",
  which reads a `StdVector<IntPtr>`, on the strength of the AHK tool reading it inline at 0x50
  stride. Both were wrong. `session-2026-08-buffs.rec` settles it three ways, and the first is
  arithmetic: over 999 frames the vector's span is 56, 64, 72, 80, 88 or 96 bytes — always a
  whole number of 8-byte pointers and only coincidentally of 0x50 structs. A span of 56 bytes
  cannot be a list of 80-byte entries. Then the structural half: dereference the qword the
  inline reading calls `entry[0].BuffDefinitionPtr` and its own first field holds **the Buffs
  component's address** — a StatusEffect knows its owner, where a `.dat` row shared by every
  entity in the game could not. And the size of it: over those frames the inline reading finds
  218 buffs in total, the pointer reading 8185.

  The failure mode is the reason it lasted. Dividing a span of 56–96 by 0x50 FLOORS to 0 or 1,
  so nothing threw, no pointer came back empty, and there was nothing to notice — the tool
  simply reported that the character had no buffs on. Every buff condition silently never
  matched, `IsFlaskActive` always said "not running", and the flask automation that depends on
  it spent charges re-using flasks that were still going. `BuffsReader` now REFUSES a span that
  is not a whole number of pointers rather than flooring it, because flooring is precisely what
  hid this.

- **`SeenBuff` crossed the wire in PascalCase.** `ConfigJsonContext` sets no naming policy —
  every record that reaches the page spells its own JSON names — and this one, alone among
  them, carried no `[JsonPropertyName]`. Nothing failed: the list arrived with the right number
  of rows and every field in each row `undefined`, so `buff.displayName || buff.name` rendered
  the string "undefined", `buff.active` was falsy on everything, and clicking a row wrote
  `undefined` into the rule. The guard is a test that serialises through a context with the
  real one's options — **without** a naming policy, since camel-casing in the test would pass
  whether the attributes exist or not.

### The atlas — the one feature that is INTERFACE rather than world

Ported from GameHelper2's Atlas2. It is the exception to the paragraph above: it reads
`UiElement`s at a fixed child path instead of a `WorldSnapshot`, so it has its own read
(`AtlasWatch`) rather than riding the world one. What it taught:

- **Two rates, because the atlas has two kinds of fact.** What a node IS — its id, contents,
  connections — cannot change while somebody is looking at it, so it is read on an interval.
  WHERE it is changes every time the atlas is dragged. Reading the first at the second's rate
  is what makes a naive port cost more than it is worth.
- **Match maps by internal id, never by displayed name.** The reference groups by "The Copper
  Citadel", which is the one string the game translates — on a German client every one of its
  groups matches nothing.
- **The game's own tags do not say what players mean.** The maps people call towers are Bluff,
  Mesa, Sinking Spire, Alpine Ridge and Lost Towers; only the last carries the `tower` tag,
  and that tag also covers the six Precursor Towers, which are a different thing. So a group
  matches by tag OR by an outright list of ids, and the shipped ones use whichever is faithful.
- **Hide AFTER asking about routes.** A map worth routing to is one nobody has reached, so
  culling the unreachable first hides exactly the routes somebody turned on — while every
  setting still reads correct. The reference records learning this; it is a test here.
- **A shut panel is not an empty one.** Closing the atlas clears the visible bit on the panel
  and leaves everything else exactly as it was — several hundred node children still in the
  tree, still flagged, still with readable positions. So "does it have any maps in it" answers
  yes to an atlas nobody is looking at, and the overlay went on writing map names over the game
  until it was opened again. The test is `UiElementReader.IsVisible` on the panel, which walks
  the whole ancestor chain because a panel shut by its container keeps its own bit set. Asked
  first, it is also what makes the read idle: a walk up a handful of parents instead of several
  hundred nodes read for nothing.
- **PoE2 has no "hovered UiElement" pointer, so hovering is geometry.** The overlay gets out of
  the way while the cursor is on a map, because the game puts its own panel over that node. The
  AHK tool had already gone looking for a pointer that answers this: the world-entity hover
  chains (`MouseOver` off InGameState, and the hover tracker) resolve AREA ENTITIES only and
  never see the interface, and two flat scans proved the client keeps no hovered-element slot
  anywhere — only whole panels are pointed at, never the leaf under the cursor. It solved the
  same problem for inventory items by descending the interface tree to whatever contains the
  cursor. On the atlas that descent is one step: every map is a child of the one panel and its
  rectangle was read this tick anyway. Tested against ALL maps rather than the drawn ones — the
  game shows its panel whether or not this overlay labelled that node, and it is the OTHER maps'
  labels and lines that would be drawn across it.
- **The map ratings are the one data file that is an OPINION.** Everything else in `data/` is
  extracted from the client or ported from a reference; `atlas-ratings.json` is a judgement about
  which maps are worth the time, which is exactly why it is a file — it will be disagreed with,
  and disagreeing should not need a rebuild. It is written by DISPLAY NAME, because that is what
  a person maintaining it can read, and resolved to ids ONCE at load through our own English
  name table — never against a string the client translates, so the ratings land the same on a
  German client. A name resolves to *every* id carrying it (three ids are all "Abyssal Depths").
  Names that resolve to nothing are kept and reported rather than dropped: a typo is otherwise a
  line somebody wrote that silently does nothing, which is indistinguishable from having
  forgotten to write it. There is a test that the shipped file resolves completely, which is
  what will catch a league renaming a map. The colour scale runs to the highest rating IN THE
  FILE rather than to a fixed ten, so any scale works — the cost is that it is relative, and
  that is the right way round for an opinion, where green should mean "the best there is". An
  UNRATED map gets its own pill — a slate `?` off the ramp entirely, never a shade of red,
  because a map nobody has judged is not a bad map and anywhere on the scale would be an opinion
  nobody holds. Telling that apart from "ratings switched off" needed one thing: the scale
  travels with the mark, so no-rating-with-a-scale means unrated and no-rating-without-one means
  the feature is off. Both were the same `null` before. The pill is sized for the WIDEST rating
  rather than for its own number and centres the text in it, so every pill on the atlas is the
  same shape — a size that means nothing otherwise reads as though it means something.
- **The biome ring goes outside the plate, so both borders can be read.** The group colour was
  already on the plate's edge, and a biome border on the same edge would mean the thing somebody
  switched on quietly hides the thing the map IS. The reference draws its biome border outside
  its plate for its own reasons and that geometry solves this: the ring sits clear of the group's
  border with no overlap. The id→colour table is ported from `Plugins/Atlas2/json/biome.json` and
  cross-checked from the other side — the six "Also counts as a … Area" tablet effects in
  `data/atlas-content.json` run Water, Mountain, Grass, Forest, Swamp, Desert, exactly ids 0–5.
  Two colours are deliberately NOT the reference's, both because this is a two-pixel ring on a
  dark plate where the reference has a coloured background: Swamp carried byte-for-byte the same
  blue as Water there, and Forest at 0.0/0.266/0.097 cannot be seen at all. An id past the end of
  the table draws no ring rather than a fallback colour — a league adding a biome would otherwise
  ring every map in it confidently in a colour standing for something else. And "borders off"
  travels as `-1`, not as `0`: nought is Water, and a sentinel inside its own range would paint
  every unknown map as a lake.
- **A stale position is invisible on a label and catastrophic on a line.** The two-rate read
  keeps what a node IS for a third of a second and re-reads WHERE it is every tick; a node the
  panel did not place this tick used to keep its last-seen position, on the grounds that a third
  of a second of lag cannot be seen. On a label it cannot. Dragging the atlas re-lays every
  node, so every node that missed a tick sits exactly one drag behind — and once connections
  started reading, every line to one of them became a ray with that same offset, all PARALLEL
  because they all shared it, worse the further the atlas was scrolled. A node the panel did not
  place is a node it is not drawing, so it is dropped. The general lesson: a cached position is
  a lie whose cost depends entirely on what is drawn from it, and adding a new thing drawn from
  it can turn an invisible lie into the whole screen.
- **A route is RUNS, not a polyline.** A route can cross a map the panel has no position for —
  one the atlas has not materialised, or a grid position the edge table names that no map sits
  on. Dropping that step and carrying on sounds harmless ("the line still goes the right way,
  one corner cut") and is not: the line then runs straight across the gap, along no connection
  anybody can walk, and with several steps missing what is drawn is a straight line between two
  maps nowhere near each other. So a missing step ENDS a run and the next found step starts a
  new one — which is what the reference does, setting its previous point back to nothing rather
  than joining across. A hole is the honest answer; a bridge is an invented connection.
- **The web is between the maps ON THE SCREEN.** Hiding the finished maps and the unreachable
  ones is how an atlas is made readable; a line to a hidden map is a line to nothing. The
  contract said "between drawn maps" and the code walked every live node — which drew nought
  lines while connections read as empty, and two thousand the moment they worked.
- **The lines belong to the PANEL, not to the nodes.** One flat vector of edges — an unknown
  word, then the grid position at each end — and no node has a neighbour list of its own. Read
  at that offset on each node instead, the two words are whatever that node's bytes happen to
  be: most nodes report no connections and the occasional one reports invented ones. The
  symptom is not an empty atlas, which is why it survived a while — it is a route drawn to the
  right map along a way nobody can walk, i.e. exactly what a pathfinder that picked the long
  way round would look like.
- **A magnitude is in sixty-fourths, and it goes INSIDE the wording.** The high half of a
  content token counts in 64ths, so a plain effect — one of the thing — arrives as 64. The
  low half selects a template and a `{0}` in it is where the number goes; a wording with no
  `{0}` gets no number at all. Appending it instead put "Area contains Abysses **x64**" on
  every node of the atlas. Badges are not this: their high half is a category tag, and a badge
  never carries a count.
- **The composition is a pure function.** Everything that can be got wrong is a decision rather
  than an address, and none of it is reachable through a live read.

#### The ritual line — the one place this tool contains a game's RNG

The atlas's ritual line ("Head of the King") is drawn across several maps and each map it takes
rolls a reward. The rolls are **client-side and deterministic**, so every route the line could
take can be priced before a single map is picked — which is the feature: not "what did I get"
but "which way is worth going".

- **`RitualRandom`** is TinyMT32 as the PoE2 binary implements it, which is *not* as reference
  TinyMT does — the state transition shifts the opposite way and the seeding starts from
  constants with the pre-step applied. The textbook version is a perfectly good generator that
  agrees with the game on nothing.
- **`RitualMods`** rolls by weighted **reservoir** sampling: a draw per row against a running
  total, not one draw against a cumulative table. The pool's ORDER is therefore part of the
  answer, and the data file keeps the game's own row numbers.
- **Checked against the authority, not against a second reading of it.** The reference's
  `TinyMt32` and `PredictModPass` were lifted verbatim into a throwaway console program and run
  over 400 seeds and 576 reservoir picks; the output is committed as
  `tests/fixtures/ritual-roll-vectors.json`. That cannot prove the reference matches PoE2 — only
  a live roll can — but it proves the port matches the reference, which is the half that was
  mine to get wrong. Four mutations of the generator fail it.
- **Two rules that look like bugs and are not**, both pinned by tests: a blocked map still
  occupies its RANK among the candidates (dropping it shifts every later candidate and predicts
  the wrong reward for all of them), and a route that cannot reach full length is not offered at
  all (the game refuses a click that would strand the line, so a shorter branch is not a worse
  route — it is one nobody can walk).

**None of the atlas offsets are confirmed.** They were ported from the reference with the game
unavailable, so `schema/poe2.offsets.json` marks the three `Atlas*` blocks UNVERIFIED and the
window's **check the read** button reports what each step of the walk found — the panel's child
path, the flag fingerprints seen, how many lines the panel's edge table holds, how many children
read as maps, and the first few decoded. It is the first thing to press when the atlas is open
and nothing is drawn on it, and the line count is what to look at when the maps read fine and no
route appears: nought there means nothing can be routed to, however well the nodes decode.

When the path finds nothing, it goes further and **hunts for the panel by shape**: hundreds of
children mostly sharing one flags word is what a grid of map nodes is and what almost nothing
else in the interface is. It prints the matching child paths, marks the one whose fingerprint it
recognises, and the answer is pasted into the schema — which hot-reloads, so a drift that would
otherwise be a debugging session is an edit. This is the `drift scanner` idea from the build
order above, applied to the one feature whose offsets are entirely unverified. The ritual
offsets are worse still — they hang off that same panel, so a wrong panel path makes every one
of them read rubbish. Check that the atlas draws at all before believing anything the ritual
window says.

### The stash listing — every tab, every item, every stat

The inventory reader the two missing areas were blocked on. What it turned out to be about:

- **A stash tab IS an inventory**, in the same vector as the backpack and the worn gear. The
  tabs are not a separate structure to be found, which is the whole reason listing them is
  possible — and it is not obvious from the game, where a tab looks like a window.
- **The item list is one entry per occupied CELL.** A piece of body armour appears six times.
  Counted as they come, a tab of large items reports several times what is in it, and every
  number built on that count is wrong by a factor that looks entirely plausible.
- **Two components for one job.** An item's mods live on either `Mods` or
  `ObjectMagicProperties` — flasks and gems carry the second — with the same three fields at
  different offsets, and which one an item has cannot be told from its path. Both are read and
  the better answer wins. (Both projects independently reached 0x144/0x150 for it, which is
  about as close to confirmation as an offset gets without the game.)
- **The stats are the game's own answer**, read off the item, not recomputed from its mods.
  Adding the rolls up would need each mod's dat row to say which stat each roll feeds — not
  available — and would be a reimplementation of the game's arithmetic besides.
- **The stat key is a ROW INDEX**, not an id, so the shipped table has to cover all 27,000 rows;
  a reader cannot know in advance which an item will use. That file is 2.7 MB and is what turns
  `1043: 79` into `+79 to maximum Life`.
- **The wordings carry a format in their placeholders** — `{0:+d}` — and that is where the plus
  in `+79 to maximum Life` comes from. Filling only the bare `{0}` leaves a thousand of the
  game's own lines untouched, reading as though the table had no wording for them.

It runs **on demand**, not per tick: a full read is thousands of entities, orders of magnitude
past anything else here, answering a question nobody asks sixty times a second.

**A tab holds nothing until it has been opened in game** (confirmed by the owner, 2026-08). The
client asks the server for a tab's contents when it is opened and not before, so a tab never
opened this session reads as an empty one, and the two are indistinguishable from this side.
Every count is therefore "what has been opened" rather than "what the account owns" — so the
status names how many tabs came back empty, and an empty page says both things it might be. A
listing that quietly called its total "your stash" would be wrong for every player who has not
just clicked through the whole thing.

### The art — reading the game's own packed files

An item carries the **path** of its picture, not the picture. The stash is drawn as a stash
rather than a list, so those paths have to become pixels, and the file they name is in the
game's own bundles. Reading it there is preferred over asking a website: nothing leaves the
machine, nothing can be out of date, and it works offline. poe2db stays as the fallback for
whatever the install will not give up, and that half stays off until somebody turns it on.

Four formats stacked on each other, each taken from LibGGPK3/LibBundle3 rather than from
memory:

- **The archive**, in whichever shape an install has — a loose `Bundles2` folder, or one
  `Content.ggpk` holding the same folder inside it. Same contents, two packings, so it is an
  interface with two small implementations rather than two features.
- **The bundles**, compressed in independent 256 KB chunks with a table of their compressed
  sizes at the front. That table is the whole point: one icon out of a 200 MB bundle costs one
  chunk, and nothing here ever holds a whole bundle.
- **The index**, which files everything under a 64-bit hash of its path and keeps the
  spelled-out paths in a separate compressed blob. Finding a path means hashing it, so that
  blob is never unpacked at all.
- **Oodle**, and this is the one that does not work. The managed decoder was chosen so there
  would be no licensed library to ship; measured on a real install (2026-08-10) it cannot read
  Path of Exile 2 at all.

  **PoE2 packs with Leviathan.** The index is 565 chunks, 113,943,153 bytes in, 147,897,312
  expected out, and the first chunk begins `8C 0C`: low nibble `0xC` says Oodle, and decoder
  type `12` is Leviathan in ooz's own `Kraken_DecodeStep`. The header and the chunk table read
  perfectly in the same breath, so it is the decoder and nothing else.

  **OozSharp only implements Mermaid.** Its package blurb lists "Kraken / Mermaid / Selkie /
  Leviathan"; its source carries the comment *"Only need Mermaid for Fortnite"*, and the word
  Leviathan does not appear in it. It is a Fortnite replay decompressor.

  **And it named the codec wrong.** It reported `Decoder type Selkie not supported`, because its
  enum starts at 1 where Oodle's starts at 0 — every name it gives is one out. Selkie is a real
  codec and a wrong answer. The name in a report is therefore read off the chunk's own first two
  bytes rather than taken from whichever decoder just failed.

  **What works instead is the game's own library.** Oodle is on the machine already — the game
  cannot run without it — so `oo2core_*.dll` is loaded by path and called through a function
  pointer, which ships nothing licensed. That is also what LibBundle3 does, and LibBundle3 is
  where this bundle format came from; it has no managed decoder at all. PoE1 ships that DLL
  beside its executable. **PoE2 does not** — its folder holds bink2w64, the D3D compilers, fmod,
  Aftermath, XeSS and steam_api and no Oodle — so the tool also looks beside itself, and anybody
  with a copy can drop it there. Without one, item pictures come from poe2db.

Three things that fail **silently** rather than loudly, and are worth knowing before touching
any of it:

- **Which hash is in the file.** GGG changed it in 3.21.2, from FNV-1a to Murmur2-64A, and
  picking the wrong one finds nothing at all — it reads as an empty install. It does not have to
  be guessed: the first directory record is the ROOT, whose path is empty, so its stored hash is
  whichever function's value for the empty string, and those are two different constants. The
  file says which hashed it.
- **A directory record is 24 bytes, not the 20 its four fields add up to**, because the
  reference reads the array as a raw span of a struct the runtime pads to its alignment — and
  writes it back the same way, so the padding is really in the file. Nothing here reads past the
  first record, so it does not bite today.
- **A texture may not be a texture.** What comes out of the bundle for a `.dds` path is one of
  three things (owner, from a PoE2 install — neither reference covers it, LibGGPK3 being a
  PoE1-era tool and GameHelper2 not decoding textures at all): a **signpost**, whose content is
  `*` and then the path of the file that really holds the picture, which may point somewhere
  else again; **Brotli**, behind four bytes that are its decompressed size and not part of it;
  or **just a DDS**. All three are named `.dds`, so which one it is has to come from the
  content, and it has to be worked out again at every hop.

  The sharp edge there: a signpost starts with `*`, and one decompressed size in every 256
  starts with that same byte. Deciding on the byte alone reads a picture as a path to nowhere,
  for some textures and not others — so the check is that what follows is a plausible path, and
  a real compressed texture whose size begins with `*` is a test.

Everything except the Oodle call is tested against data the tests build themselves — a GGPK, a
bundle and an index written from the reference's description, so a disagreement between the two
fails rather than passes. The hashes are pinned to values from a second implementation written
differently, because a round trip through one implementation agrees with itself however wrong it
is. The textures' Brotli ships with the framework, so those tests compress real bytes rather
than standing in for it. Oodle cannot: there is no compressor to make a fixture with, so it is
handed in and proving it needs real bundles.

**Where the install is** is asked of the game itself — the tool is already attached to it, and a
running process knows where its own executable is. The usual folders are only a fallback for
reading with the game shut, and a folder counts because the files are in it, never because of
its name.

### Prices — what the stash is worth

The stash listing says what is in every tab; this says what it is worth. Everything is in
**Exalted Orbs**, because the API quotes in Divine and one number has to be the unit.

- **Two keys, and choosing between them is the whole join.** Anything fungible is found by its
  **art**: every exchange line carries the file name of its icon and an item in memory carries
  the path of the same icon, so currency matches without either side knowing what it is called
  — which works on a client running in any language, with no name table to ship. Uniques have
  no such handle (every Astramentis draws its base's picture) and go by **name**.
- **A unique's name comes from its ItemVisualIdentity id**, not from what the game painted and
  not from its metadata path. The id is an engine identifier: it stays English on a localised
  client, and it is per-unique where the path is not — Morior Invictus and Tabula Rasa share
  one. `data/unique_ivi_name_map.tsv` turns it into the English name price sites know.
- **Only uniques are asked for by name.** The listed half also holds tablets, which would be
  found by their base type's name — and so would any ordinary item whose base is spelled like a
  listed line, which would then wear that line's price. Tablets going unpriced is the cost, and
  it is the cheaper one: an item with no price shows none, an item with a wrong price is
  believed.
- **The gates are measured, not chosen.** poe.ninja's raw API carries price-fixed lines its own
  website hides; on a young league the two populations barely overlap. A gate of 100 listings
  and 1 Divine of traded volume is where the cliff is on real data. Both fail OPEN when the
  field is missing, so a schema change costs the gate rather than every price.
- **The rate comes only from an answer that had prices in it.** Every response carries one,
  including the empty ones, and those are stale — measured live, 581 against 932.9 on the same
  league in the same minute.
- **Which league is asked of the game**, on the same two-hop resolve the inventories use, and
  spelled exactly as poe.ninja spells it (`HC Runes of Aldur`). Typed in as a setting instead it
  goes stale at every league start, silently, and stale prices look exactly like real ones.
- **A change of league throws the book away rather than ageing it.** Those prices are not old,
  they are a different economy, and shown next to the right ones they look just as trustworthy.
  The same check guards the file the last session left.
- **A bad answer never replaces a good book.** "The numbers vanished" is a worse failure than
  "the numbers are twenty minutes old", so a refresh that comes back empty leaves the previous
  prices alone, and one failing kind costs that kind rather than the other twenty.

It is **off until somebody turns it on** — more firmly than the item art, because there is no
local copy of a price to prefer: they exist only on somebody else's server. What goes out is a
league name and nothing else. The refresh runs in the background and a drawn frame reads
whichever book is finished, so asking what something is worth never waits for anything.

Every total is shown with **how many items it could not price**, because a total on its own
reads as "what this stash is worth" and a book covering a fifth of it would answer that with a
number nobody can see is wrong.

### The trade site — the uniques poe.ninja has nothing on

poe.ninja has no unique prices on Standard at all, and on every league the listing gate drops
the uniques with few listings — which is most of the interesting ones. The gap is filled from
the game's own trade site, for uniques only, and only for the ones the book could not price.

**It cannot be asked over plain HTTP, and that is not something to work around.** The `trade2`
endpoints are Cloudflare-gated: a session cookie on its own gets a 403, and passing the check
wants the browser's cookies, its User-Agent *and* its TLS fingerprint together. Any HTTP client
that gets through is imitating a browser, which is the thing the check exists to catch.

So the request is made **by** a browser. A WebView2 window opens on the trade search page, the
player signs in once, and an injected helper runs the two-step query — `POST
/api/trade2/search/poe2/<league>`, then `GET /api/trade2/fetch/<ids>?query=<id>&realm=poe2` — as
a **same-origin fetch with `credentials: 'include'`**. Cloudflare is satisfied because nothing
is being impersonated, and **no secret ever reaches this side**: the sign-in lives in that
window's own browser profile, and what crosses back is asking prices. The cookie-manager route
was available and deliberately not taken. Contract and transport both from the AHK tool, which
has been running them since 2026-06.

- **A single listing is not a price.** "Median of the cheapest few" over one listing *is* that
  listing, which is how a starter unique ends up quoted at four thousand Divine — the same
  illiquidity the poe.ninja gates exist for, arriving by a different door. Below three
  convertible listings the answer is "unpriced".
- **The cheapest is not the price either**, so it is the median of the cheapest eight: the
  cheapest listing is as often a mispriced item or a seller logged off as it is the market.
- **A listing in a currency the book cannot convert is dropped**, never guessed at. Exalted is
  one by definition and Divine comes from the rate; everything else has to be a line in the
  book under the id poe.ninja gave it. The two vocabularies look like the same short slugs, but
  that is a join rather than an assumption — a miss costs the listing, not a wrong price.
- **Every answer is written down, the empty ones included.** A unique nobody is selling answers
  with nothing, and forgetting that means asking again on every stash read, forever, at three
  and a half seconds a query.
- **One query in flight, spaced apart, and five minutes of silence after a sign-in wall.** 401
  and 403 mean the browser is signed out, and asking again in fifteen seconds only gets refused
  again at somebody else's front door.

It has **its own switch**, separate from poe.ninja's, because they are different things to
agree to: one is an anonymous request for a league's prices, the other opens a browser and asks
somebody to sign in to their own account.

The transport is **handed in** — the price layer takes a "name and league in, listings out"
delegate — so all of the deciding is testable without a browser or a network, and the one place
that talks to the trade site is visible from the constructor. It is also what keeps the layering
honest: the queue lives in Features and the browser in Config, which Features cannot see.

### Quests — what every one of them is waiting on

The in-game tracker shows the quest you are tracking. This shows all of them, because the
question worth asking is "what have I left behind" rather than "what am I doing" — the act-two
side quest nobody remembers starting is exactly the one the tracker will not surface.

It is the same state machine the game's own tracker runs on, so the answer is the same answer,
for every quest at once and readable from a recording afterwards. Two halves have to meet: what
the character has DONE, which is in the process, and what the quests ARE, which is in the
install's own data files.

What follows is WHY the design is what it is. **[reading-quest-steps.md](reading-quest-steps.md)
is the HOW** — every offset, struct and column of the pipeline, with the check that proves each
stage, in enough detail to re-derive it or fix it when a patch moves something.

#### The bitset — six sessions, and why none of them could have worked

**`ServerData → +0x60 → +0x188 → +0x248`, and it is a sparse bitset over QuestFlags row
numbers.** The vector at the end holds 9-byte records: one byte of chunk index, then eight
bytes of flags, sorted and strictly increasing. Bit `n` of chunk `c` is quest flag row
`c * 64 + n`, and that row number is the same number the tables use as a foreign key — so the
join is arithmetic and there is no name matching anywhere in it.

This was the longest-standing open question in the project, and it did not fall to a hunt. It
was handed over as a pointer chain, and the value of the six sessions spent on it is entirely in
knowing WHY each approach was doomed:

- **Reference sweeps find nothing because there are no references.** `--questflags` looked for
  pointers to QuestFlags rows. The set stores bit positions. A row's ADDRESS never appears
  anywhere near it, so the sweep was searching for a thing that does not exist.
- **Page-diffing is defeated by the container, not by the data.** The vector is sized exactly,
  so setting a flag in a chunk that is not present yet inserts a record and reallocates the
  whole buffer. Every byte moves. "Diff memory across doing one quest step" therefore reports
  the entire region as changed, every time, and the one bit that actually flipped is invisible
  in the noise.
- **A four-byte scan for a flag value is a category error.** There is no per-flag variable to
  find. The trap at the top of this file — 999/1000 turning out to be pointer high-dwords — came
  out of exactly that search.

**It is verified by naming rows, not by looking plausible.** The set read out of a live session
is joined back against `QuestFlags.datc64` and the Ids printed: row 11 is
`CompletedSkillGemTutorial`, row 22 is `CompletedLifeFlaskTutorial`, row 46 is `EnteredHideout`
— all three things the character had demonstrably done, and none of them a thing a wrong chain
could name by accident. A bitset off by one bit would name the neighbour instead.

The fixture also catches BOTH ways a flag can arrive, which is what makes it a regression test
rather than a snapshot: flag 1771 was set in chunk `0x1B`, which the set already held (it
carried bits 31, 32, 44 and 46 before), while flag 644 arrived as a brand new chunk `0x0A` — the
reallocating case that defeated the page-diffing.
`tests/fixtures/session-2026-08-questflags-set.rec` is that session and `QuestFlagSetTests`
replays it.

#### The tables — `.datc64` out of the install

The quests themselves are not in memory, they are in the game's own data files, read through
the bundle reader the item art already needed. Four tables: `Quest` (127 rows), `QuestStates`
(1477), `QuestFlags` (5717) and `MapPins`.

The format is a `u32` row count, that many packed fixed-width rows, eight bytes of `0xBB` as a
separator, and then a variable-length section holding every string and array. Three things in
it are sharp:

- **Offsets into the variable section count from the SEPARATOR, not from the file.** Which
  makes offset 0 mean "no string" rather than "the first string", and reading it as a position
  hands back the `0xBB` padding as a run of U+BBBB. That only showed up once two text columns
  were compared against each other — on its own it looks like a decoding problem.
- **The separator has to be searched byte by byte.** `Quest`'s rows are 119 bytes, so its
  separator lands at an odd offset and a scan that steps four bytes at a time walks straight
  past it. That table read as "not a .dat file at all" until the scan was fixed.
- **The vendored column list will not always add up, and that is not a reason to reject a
  table.** `Quest` measures 119 bytes per row where the schema's columns compute 103 — there are
  columns the public schema does not know about. So a row size that disagrees is settled by
  ASKING the table: read the columns the feature actually needs as strings and see whether they
  come back as text. A prefix that reads correctly is worth more than an arithmetic identity,
  and padding the column list until the numbers match would move every offset after the padding.

The layouts live in `data/quest-tables.json` rather than being fetched, and the file records
what was measured. Column widths follow dat-schema's rules — `foreignrow` is 16 bytes, `row` is
8, an array is 16 — and `MapPins` is deliberately OPTIONAL: without it the quest steps still
read, they just name no place, which is better than not reading them at all.

#### The join, and the two things the game had to settle

`QuestStates` is a state machine. Each state names the flags that must be PRESENT and the ones
that must be ABSENT, and the state whose conditions hold is the current objective. Two
properties of it are not derivable from the schema and were measured against the game instead —
both of which had already produced a wrong answer that looked right:

- **`Order` counts DOWN.** The last state of a quest is 0. Sorting it the other way ran every
  quest backwards: a FINISHED quest reported its completion state as the current objective with
  an earlier step as "then", and a quest genuinely in progress reported itself finished and was
  hidden — which is why two quests the game's own tracker was listing were missing from the
  window entirely. Every synthetic test passed throughout, because the fixture modelled the
  direction backwards too. It took a screenshot to catch.
- **`Message` is what the panel renders, not `Text`.** The two columns are just a string each
  and the schema does not say which is which. Shown side by side against the game, `Message`
  matched word for word — "Find the Red Vale" — while `Text` was the longer sentence every
  time. So `Message` is the objective and `Text` is the detail under it, which is usually the
  half that says where to go.

**Several states holding at once is ordinary**, and it cost a screenshot to establish that too.
Most states declare only the flags that must be present and none that must be absent, so every
state the character has already passed goes on holding. The furthest along in progression order
is the answer. It is still counted and shown, because a sudden jump in that number is what a
mis-read condition column would look like.

**The route folds its branches.** A quest with branches carries a state per branch — The
Runeseeker has 87, most of them the same sentence for the different regions it can be done in —
so consecutive states wording an objective identically collapse into one line with the count
beside it. Only CONSECUTIVE ones, so the order is never rearranged to make the fold look
tidier, and the fold gathers every PLACE those states named: the sentence is what they had in
common, the region is what they differed in, and the wall of identical lines was hiding exactly
that. The ordering is stable for the same reason — branch states share an `Order`, and an
unstable sort would put ties in a different relative order on different reads.

#### Map pins, and a question deliberately left open

`MapPins` carries its own flag conditions — `QuestFlags1/2/3` as arrays, `QuestFlag1/2` singly —
so the same bitset says which pins the game will draw. The useful half is the INVERSE of what
the map shows: which pins are visible the map answers itself, but which flags a hidden pin is
waiting on is visible nowhere, because a locked pin simply is not drawn.

**What those five columns mean is not known, and no rule is baked in.** Unlike `QuestStates`,
whose two are named `FlagsPresent` and `FlagsMissing`, nothing says which way any of the five
point — all required, alternatives, or one of them an exclusion. So both readings are counted
side by side in the tab, and comparing them against the number of pins the world map actually
draws is what will settle it. One measurement rather than an argument, which is the same way
`Order` and `Message` were settled. An empty condition counts for NEITHER reading: four of a
pin's five columns are usually empty, so calling those satisfied would show every pin and
calling them unsatisfied would lock every one.

**What none of this can do**, said because the feature invites the assumption: `Text` is what
GGG wrote and nothing derives more than that. "Find the Hooded One" is as specific as the data
gets, and which room he is in is not in any table. `MapPins` has no coordinates either — a
name, a world area and an act — so a pin answers WHICH PLACE and not which point. It is the
sentence "and it is over there", and it can never become a marker in the world.

### Remaining

More features on the slice, and configuring them from the page.

Loot tracking with prices and moving items into a container both now have the reader they were
blocked on; what they still need is the game running to verify it against.

The packed-file reader needs the same: the formats are pinned against built fixtures, but
whether PoE2's own bundles decompress, and what its textures are actually block-compressed
with, only a real install can answer.
