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

**And a third way a replay misleads, found 2026-08 while mining the committed fixtures for
offsets nobody had decoded: a region swept ONCE reads as constant forever.** The replay serves
the newest bytes at or before the current frame, so a block read at frame 5 and never again
answers every later frame with those same bytes — which is indistinguishable from a field the
game never writes. Every session in `tests/fixtures/` sweeps `WorldData` 0x000–0x840 exactly
once (MatrixHunt's sweep) and then reads only the matrix per frame, so 258 of its 264 recorded
slots "never change" and the six that do are the matrix. That is a fact about the recorder,
and reading it the other way round would have produced a confident claim that the camera's
frustum is static — while its numbers plainly differ between recordings. **The check costs one
question: how many distinct reads actually covered this address?** One means the replay is a
photograph, and the only claims it supports are about the instant it was taken.

That instant is still worth a great deal, and this is the useful half of the lesson: **the
same single sweep is present in eighteen independent files, from five game launches**, so a
geometric claim can be tested eighteen times over even though no single file can time it. See
`CameraFrustumTests`.

**What a recording can say about a field nobody ever read: whether it fits.** The third state
between "confirmed" and "unanswerable", and the reason the pool cells in the schema are worth
measuring. Components of one class come out of one allocator, so sorting the addresses a class
resolved to and taking the smallest gap gives the cell — 0x420 for `Life`, 0x550 for
`Positioned`, 0x630 for `Render` — and every other gap being a whole number of cells is what
makes that a measurement rather than one lucky pair (618 of 618 for `Life`). An offset past the
cell cannot be in the object; one inside it has room. That is the entire evidence for the three
`Life` vitals nothing in this project has read — `Ward`, `Divinity` and `Spirit`, the last of
which is a PoE2 resource with no PoE1 counterpart — and had the cell come back 0x280 it would
have refuted them without a byte of them being read.

**The same cells cost a documented conclusion its footing, and that is the sharper lesson.**
One component containing two identical sub-objects and two components lying side by side in a
pool are *indistinguishable from inside the object*: the same vtable one cell on, the same
vector, the same inline buffer. `Inventories` was read the first way and written down as an
array with a 0x150 sub-object stride, on three pieces of evidence that are all equally true of
the second reading. What separates them is outside the object — ask the **entity list** who
owns the address one cell on. Over 22,363 sightings it belongs to a *different* entity 16,560
times and to the same one **never**, and the same test comes back the same way for `Life`,
`Buffs`, `Positioned`, `Render` and `Actor`, with zero same-entity repeats anywhere. It also
closes two loose ends for free: `Inventories+0x158`, recorded as an open puzzle because it held
"something other than the entity +0x008 holds", is the *neighbour's* `OwnerEntity`; and the two
live-looking pointers at `Actor+0xF18`/`+0xF20` are 0xCE0 + 0x238 and 0xCE0 + 0x240 — the next
actor's `SkillActionPtr` and `MoveActionPtr`. See `ComponentPoolTests`.

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
system, **launcher**, DI container, interfaces with a single implementation,
inheritance-based feature classes.

The launcher is the one worth separating from the updater it used to be paired with here,
because only one of the two is a structural cost. A launcher is a second program that owns
the first — a process to start, a window to manage, a place for configuration to drift to.
An updater is a few hundred lines that read two small files and unpack a zip; it adds no
process, no window, and nothing any other feature has to know about. What made the pairing
look right was GameHelper2 shipping them together. See **Updating itself** below for what
this one actually is.

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

## Updating itself

Every push to `main` replaces the rolling `latest-dev` release, and until now the only way
to take one was to notice, download a zip and unpack it over the folder by hand. The cost of
not noticing is specific to this kind of tool: **an offset that drifted with a game patch is
fixed in a build somebody is not running**, and the symptom is not "there is an update" — it
is half the features quietly reading the wrong bytes.

**The build has to be able to say what it is, and nothing already in it could.** The tag is
`latest-dev` for every build ever made, and the assembly version has been `1.0.0.0` since the
first commit. So the publish workflow writes a **`version.json`** next to the executable —
tag, commit, build time, run number — and uploads *the same file* as a second release asset.
The check is then a comparison between two stamps written by one step of one run, rather than
meaning read into a timestamp (`Features/BuildStamp`, `Features/UpdateCheck`).

The rejected shortcut is worth recording, because it is the obvious one: treat the zip
asset's `updated_at` as the build time. It is wrong in the direction that matters — the
upload finishes *after* the build, so the running build compares as older than itself and
every launch offers an update to the copy already installed. A release with no stamp is
therefore answered with "cannot compare", never with a guess.

Four steps, and the last two are asked for:

1. **Check** — two requests every six hours: the releases API (which also carries the
   changelog, as the release body) and that release's `version.json`.
2. **Notice** — a dot on the config window's Update tab, and the overlay's own section
   header on the Status page changing to "Update available". The notice has to reach
   somebody who is playing, not somebody who happens to have the settings window open.
3. **Download** — into `update/`, unpacked into `update/staging/`, and checked for the
   executable before anything else happens. **Nothing outside `update/` is touched by the
   running tool**, and it could not be: the executable is running, and its image and the
   native libraries beside it are locked. Staging is also the integrity check — every zip
   entry carries a CRC32 and the extractor verifies it, so a truncated download fails in a
   scratch folder rather than halfway through an installation.
4. **Install** — a batch file (`Features/UpdateScript`) that outlives the process: it waits
   for the pid, `robocopy /E` (never `/MIR` — that would delete `config/`), and starts the
   tool again with the switches it had, plus `--updated <commit>` so the new build can say
   what happened. A failed copy starts the *old* build with `--update-failed`, because an
   update that goes wrong must not present as "the tool did not come back".

`--record` is the one switch not carried into the restart: it names a file, and restarting
would truncate a recording somebody deliberately captured.

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
PoEformance.App --record s.rec --hoverhunt    # + read the hovered-entity chain and the boss byte
PoEformance.App --record s.rec --sweep        # + read four components nothing has a layout for
PoEformance.App --record s.rec --inventories  # + read every inventory whole, hunting the tab's sort
PoEformance.App --record s.rec --glossary     # + find every loaded dat table and read the glossary
PoEformance.App --record s.rec --tables       # + list them, with the row size each one reports

# Reads the INSTALL, not the process - no game running, no fight to survive:
PoEformance.App --groundtypes                 # what each ground-effect type row actually is

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

### The decoy has a name: the camera's frustum sits sixteen bytes before the matrix

The block that broke the first matrix invariant was written off as "an unrelated block of four
unit rows". It is nothing of the kind. `WorldData+0x10C` holds **six clipping planes** at a
stride of 0x10 — a unit normal and a distance each, normals pointing inward — and `+0xAC` holds
the **eight corners of the same frustum** in world units. Reading a `mat4x4` at 0x11C therefore
takes planes 1 to 4, and the unit vector the old invariant demanded at `+0x30` was plane 4's
normal. The decoy was never unrelated; it was the camera describing its own shape, immediately
before describing its own projection.

None of the evidence for that is unit length, because this file already records unit length as
worthless. It is three things a wrong reading fails:

- **The corners fit the planes.** Each corner of a frustum lies exactly *on* three of its six
  faces. At `0xAC` all eight do, in all eighteen committed recordings; read four bytes earlier
  or later, not one of them lies on any, in any recording.
- **The player is inside all six**, in every recording — which he must be, since he is on
  screen.
- **The player's distance to the vertical pair of side planes is the same number in every
  recording** — 450.3 and 644.4, nineteen sessions, no exception, at world positions six
  thousand units apart. That is what a frustum carried rigidly by a player-centred camera has
  to look like, and it is not something an accidentally-convex block of floats can be. (The
  *horizontal* pair is not one number, which this section used to claim: it takes 1140.1/1140.1
  in sixteen sessions and 1326.5/907.2 in three, and the two do not sum alike, so it is not a
  pan. Something about the viewport differs and nothing here has identified what.)

A fourth reading corroborates from outside: plane 4 and plane 5 are exact negatives, so they
are the near/far pair and plane 4's normal is the view direction — `(0.466531, 0.466531,
0.751464)`, identical across all five game launches, with its two horizontal components
**equal**. The camera looks precisely along the world diagonal, which is independently what
`ScreenBasis` recovers from the matrix when it reports up-screen as `(0.7071, 0.7071)`.

#### The frustum and the matrix check each other, and that catches a live decoy

The eight corners of the view volume *are* the eight corners of the viewport, so a correct
matrix must send every one of them to an edge of normalised device space. It does — **worst
corner 0.0000 off the edge, in all eighteen recordings**. Two independent descriptions of one
camera, read from two places sixteen bytes apart, agreeing to the float.

This is the first check on this projection that needs **no scene and no threshold anybody has
to agree with**. "Is the player centred" passes for a matrix that collapses the world. "Do the
entities spread out" needs entities, and a number somebody argued over. This needs the camera
to be self-consistent, and a matrix four bytes out of place is not.

It was not a theoretical improvement. On `session-2026-08-monsters.rec` the hunt's own ranking
puts **`0x12C` read transposed** first — linearity 0.9791 against the true matrix's 0.9539 —
and `0x12C` is **plane 2 of the frustum**. A person following that line would have moved the
schema off a working offset onto a block of clipping planes: the `0x11C` mistake again,
arriving this time through the check that was written to replace the one `0x11C` got past. So
`MatrixHunt` now leads its recommendation with frustum agreement and names the higher-scoring
candidate as a decoy, and `WorldToScreen.Clip` exists so both readers of that matrix share one
convention rather than each re-deriving it.

The clip `w` turns out to be worth a name too: measured against the frustum it is the distance
along the view axis in **world units**, which is why the near and far clip distances come out
round (near 140–200, far 3200–5500 across the sessions). The projection was already computing
it and throwing it away.

`WorldReader` reads the block beside the matrix every frame — one 192-byte read, since the
corners and planes are contiguous — and puts it on the snapshot.

**And that settled the last open question: the game rewrites the frustum every frame.** The
first eighteen fixtures swept the region once, so a replay of them could not tell "constant"
from "photographed" (see the recording limits above), and everything built on the block was
provisional. `session-2026-08-frustum.rec` is the capture made afterwards, and the recorder
answers the question by construction — it drops a read whose bytes match the last one written,
so **a read in the file is a read that changed**. 669 stored reads, 669 distinct values,
changing frame for frame with gaps identical to the matrix's own. The frustum is as live as the
projection it belongs to.

One caveat survives: the matrix and the frustum are two separate reads, so they can straddle a
game write. Over 1108 frames the corner-to-edge agreement has a **median of 8.5e-6** with two
frames at 6.4e-2 — tearing, not drift, and far below anything that matters.

#### Skipping the reads for what the camera cannot see

The saving the frustum was worth reading for. `ReadAim` and `ReadMonsterBuffs` are the two
switches this project already describes as costing a read per monster, and both feed things
drawn *over* the monster — a ray from its feet, icons above its head. A monster off screen is
drawn nowhere, so the read buys nothing, and `SkipOffScreenReads` (on by default) skips it.

A culling gate is the shape of change that goes wrong silently: it can only remove work, so
when it removes the wrong work nothing crashes — a ray stops being drawn and the first anybody
hears of it is that the overlay "feels unreliable". So it is not shipped on the argument that
it ought to be equivalent:

- **The gate and the overlay decide the same thing by different routes.** What gets drawn is
  decided by projecting; what gets read is decided by the frustum. Over the eighteen
  recordings, at the frame each one actually read the frustum, the two agree on **all 1042
  tested points** — every entity's feet and the top of its model — with nothing on screen yet
  outside the frustum *and* nothing inside the frustum yet off screen. Not luck: the corners
  project onto the viewport's edges exactly, so the two are one predicate computed twice.
- **It fails open.** No frustum — an old recording, a drifted offset, a loading screen — and
  nothing is skipped. An unreadable field must never be able to switch a feature off quietly.
- **`ReadActions` is deliberately not gated.** It feeds evasion, which asks about danger to the
  *player*: a boss commits a beam from outside the view volume and you are still standing in
  it.
- **The margin is sized, not chosen.** The fastest the player crosses the world in any
  recording is 2045 units/s, so at the reader's 30 Hz the camera moves ~68 units per tick; 250
  is three and a half ticks of headroom for a frustum the game may update a frame or two behind
  the matrix.
- **The saving is counted, not felt.** `ReadCost` carries it and the status line prints it, so
  a gate that starts culling the visible world is something you can see rather than suspect.

Two things it does change, stated because "provably free" would be too strong: the Tracker's
action census is titled "what the monsters **on screen** are doing", so gating makes it match
its own description; and its buff-name list, which exists so a name can be copied into a rule,
narrows to visible monsters.

**The safety net caught something on its first run**, which is the best evidence it works.
`MonsterActionsSettledTests` went red: `ActionHunt` builds its own `WorldReader` and inherited
the default, and the published monster-action numbers — 1649 aimed actions agreeing with the
facing to a median of 1.6°, 210 completed moves — are a sample of *what the game did*, not of
what was on screen while it did it. An instrument that measures the world reads all of it, so
the hunt opts out explicitly. Anything else that measures rather than draws must do the same.

**How much it saves is now measured too, on the same capture: 46%.** 528 of 1147 hostile-monster
reads over a 53-second map session, with a frustum that is fresh on every frame — so unlike the
figures this paragraph used to carry, it is a saving rather than an artefact of staleness. And
the agreement holds at that scale: over 1108 frames and **134,572 tested points**, four points
were on screen while outside the frustum, the worst by **1.37 world units** — an eighth of one
grid cell, and 1/182nd of the 250-unit margin the gate ships with. Nothing was inside the
frustum yet off screen at all.

The same pass over the fixtures also turned up the component **pool cells**, the `Vital`
back-pointer at `+0x08` (the component's own address, on every vital of every monster — a check
a wrong base cannot pass), three `Life` vitals nothing has read but which the cell leaves room
for, and a list of what the game actually attaches to entities: **twenty component names with
no struct in the schema**, four of which are the open danger-model questions wearing labels —
`LimitedLifespan` on 501 entities, `GroundEffect`, `DiesAfterTime`, and `Beam` on
`Metadata/Effects/BeamEffect`, the boss beam that made steering necessary. Not one of them can
be decoded from the committed files, because no build has ever read a byte of any of them.
That is the standing rule saying, for once, exactly which capture to make next.

### Two candidates that were never wrong, only never asked

`--hoverhunt` exists for a failure mode this file already names and which had quietly claimed
two offsets: **a replay only serves reads that actually happened**, so an offset no build
touches is absent from every session ever captured, and no amount of recording settles it.

- **The hovered entity.** GameHelper2 walks `InGameState+0x300` → `+0x3F0` → `+0xA8`. This
  project had written the question off — but that verdict came from a hunt looking for a
  hovered *UI element*, which found a resource record instead. A hovered *entity* is a
  different question and was never asked. The first two hops resolve against this client; the
  third reads zero, which is equally what "nothing hovered" and "wrong offset" look like, and
  the only sessions holding those bytes were `--questflags` captures in which nobody was
  hovering on purpose.
- **The boss byte.** `Monster+0x27` has sat in the schema as an unverified hypothesis for
  months, and nothing reads it: `MonsterSigns` derives `IsBoss` from **rarity** instead, so the
  field has never been exercised once.

The switch reads a **window** of each object rather than the two candidate slots — the same
argument `--questflags` records regions it does not understand — so a wrong reference offset
stops being fatal to the capture and the right slot can be hunted offline afterwards. For the
boss byte it reads the whole Monster component, and the pool-cell measurement is what says how
much that is: the cell is `0x30`, so forty-eight bytes *is* the component.

It wants a person doing something deliberate, like `--actionhunt` before it: hover a monster,
hold, move onto empty ground, hover another. **The off-target moments matter as much as the
on-target ones** — a slot that never changes is not an answer, and only the contrast separates
"nothing hovered" from a wrong offset.

`HoverHuntTests` pins the property that matters before any capture exists: on a file that
cannot answer, the hunt reports being unable to rather than reading absent memory as a result.
A hunt announcing "the byte is 0 on every monster, so 0x27 is wrong" from a file that never
read the byte would be worse than no hunt at all.

#### What the capture came back with

`tests/fixtures/session-2026-08-hoverhunt.rec` is that session: 940 frames, 66 seconds, a Vaal
area with 96 monsters in it, hovering monsters, a chest, a checkpoint and a ground item on
purpose. It settled one of the two questions and — more usefully — is a clean example of why
the other one is *still* open despite thousands of readings.

**The hovered entity: confirmed.** The host resolved on 932 of 940 frames, its `+0x3F0` on all
932, and the entity slot behind that held something on 143 of them. The claim worth making is
not "the slot holds a pointer" — a wrong offset into a live object holds pointers all day — but
that **143 of 143 non-null readings are addresses the game itself was listing in `AwakeEntities`
on that same frame**, across ten distinct entities of four kinds, with the slot taking 50
different values over the session. The reading that would otherwise fit — a nearest-entity or
last-targeted slot — is ruled out by the *emptiness*: it was null on 789 of 932 frames, in an
area holding 96 monsters. A proximity slot is never empty; a cursor over floor always is.

It is read in production now (`MouseOverReader`, `WorldSnapshot.Hovered`), three reads a frame,
for the same second reason the frustum is: a slot read every frame lands in every `--record`
session frame by frame, and this one was only settleable because a capture finally contained it.
The schema carries the two hop structs, and `MouseOverHostPtr` deliberately has **no invariant**
— the drift report counts an unreadable field as a failure exactly as loudly as a wrong value,
and every fixture committed before this one predates anything reading the slot, so an invariant
there would turn the whole baseline replay red without a byte having moved.

**The boss byte: not answered, and this is the interesting half.** The run read `Monster+0x27` on
every monster in the area on every third frame — 14,462 sightings, **every single one zero**,
across Normal, Magic and Rare. That table looks exactly like a refutation and is not one: *there
was no unique monster in the area*, and zero on every non-unique is precisely what a working
boss flag reads. A hypothesis is only refuted by the case that separates it from its
alternatives, and that case was never on screen. `Report` says which case was missing instead of
concluding from its absence, and `HoverHuntTests` pins the sentence it may not print. **The next
capture needed a boss in front of the cursor** — see below, where it got one.

**And one thing the windows found that nobody was looking for.** *Two* slots on the sub-object
separate hovering from not: `+0xA8` and `+0xC8` are both null in every idle frame and non-null
in every hovering one, so either would serve as "is something hovered". They differ in what they
hold, and the second was then pushed as far as one recording goes. It exists here only because
the hunt recorded a window it did not understand — the argument for windows, made a second time.

What the file settles about `+0xC8`, without a new capture:

- **It is reallocated per frame, not per hover.** 135 distinct values across 143 hovering
  frames, and it changed on 108 of the 115 frame pairs where *the same entity stayed hovered*.
  That kills the obvious guess — a tooltip or highlight record built when the hover starts.
- **It is no entity's component.** 0 of 143 values match any address in the game's own component
  tables, which this capture contains for every listed entity.
- **It is not a `make_shared` pair.** The last slot hunted in this project (`+0x2358`) resolved
  as `_Ptr` always sitting at `_Rep + 0x10`; here no slot in the whole `0x400` window holds
  `+0xC8`'s value offset by any small constant, and it does not point inside the sub-object.
- **Shape:** every value 16-byte aligned, neighbouring ones `0x10`–`0x40` apart.

So: a small object the game rebuilds every frame while the cursor is on something. What it
*holds* was the one question left, and **no recording committed at that point contained a byte
of it** — the pointer was captured, its target never was. `--hoverhunt` follows it with a ladder
of window sizes (`0x200 → 0x80 → 0x20 → 0x10`) rather than one size, because a single read is
all-or-nothing and a too-large window off a small object records nothing at all.

#### The second capture, which closed both

`tests/fixtures/session-2026-08-hoverboss.rec` is 525 frames in front of a map boss, made with
the build that follows the companion pointer. It is a good argument for asking two questions per
capture: it answered both, and neither answer was available from the first file at any price.

**`Monster+0x27` is REFUTED, and the field is gone from the schema.** The area held
`Metadata/Monsters/MudBurrower/MudBurrowerHeadBossMAP2__@70` — Unique rarity, the word *Boss* in
its own metadata path — for 142 sightings, and **the byte is zero on it too**. A flag that is
clear on that monster is not a boss flag. Note what did *not* refute it: the first capture's
14,462 zeroes, which were entirely consistent with the hypothesis. It took one boss, not more
data. Nothing broke when the field went, because nothing used it — `MonsterSigns` derives
`IsBoss` from rarity and always has — and `Monster` now stands as a component with a measured
pool cell and **no fields at all**, which is the finding rather than a gap. The hunt still reads
and reports the offset, so the next boss re-checks a refutation that currently rests on one.

**`sub+0xC8` is EXPLAINED, and it is worth nothing.** Its target is a **16-byte object**: `+0x00`
is a single module address across the whole session — one class — and `+0x08` is **the hovered
entity plus `0x100`, on 126 of 126 frames**, for the boss and for a ground item alike. It is a
per-frame handle wrapping an interior pointer into the very entity `+0xA8` already names. Every
negative from the first file survives and now has an explanation: of course it is no component,
of course it is reallocated every frame, of course it is not a `make_shared` pair.

Everything past `+0x10` of that object is **neighbours, not fields**: those offsets carry several
different module addresses across the session where `+0x00` carries exactly one, and most of them
point back into the same small-object arena. Decoding them as this object's members would be the
`Inventories` mistake in a new costume — one object's window read as its own sub-structures when
it was the allocations beside it.

`Report` now *re-checks* the identification rather than restating it: one vtable, and the payload
at the fixed offset, or it says the layout changed and to hunt it again.

### The four components with no layout anywhere

`--sweep` is the next capture, and the first question it settled was one about the *references*
rather than the game. `LimitedLifespan`, `DiesAfterTime`, `GroundEffect` and `Beam` are the four
unmodelled components the danger model actually needs, and **neither reference has a single field
for any of them.** GameHelper2 registers layouts for 21 components and treats these as *markers* —
its own note calls them "components present on entities but with no registered layout", and
`Entity.cs` uses `DiesAfterTime` purely as "is this a temporary monster". The AHK tool's
`DecodeDiesAfterTimeComponentBasic` reads the two header fields and stops. So there is nothing to
copy and nothing to verify: this is a blind sweep, decoded afterwards against the file.

**All four at once, and the numbers are why.** Each is tiny — measured pool cells `0x60`, `0x50`,
`0xC0`, `0xA0` — so "the whole component" is complete by construction rather than a judgement
about where to stop, the same argument that made `Monster` cheap to be thorough about. And they
are not many at a time: across every committed recording the concurrent maxima are **39, 3, 11 and
5**, so reading every carrier of all four costs under 9 KB a frame at the worst moment on file.
There is no saving from doing them one at a time, and a real cost — these things appear during
fights and the situations overlap, so four captures would be four fights and four chances to miss.

Everything above was measured from the existing recordings **without reading a byte of any of the
four**, which is the pool-cell trick applied a second time. What else it yielded, from the entity
list alone:

| Component | Carriers | At once | Measured lifetime |
|---|---|---|---|
| `LimitedLifespan` | `Effects/Effect`, `BeamEffect`, `ServerEffect` | 39 | median **0.5 s**, tail to 30 s |
| `DiesAfterTime` | `Monsters/Totems/DarkEffigyTotem` | 3 | **7.2–19.1 s** |
| `GroundEffect` | `VisibleServerGroundEffect` | 11 | **13.8–49.9 s** |
| `Beam` | `BeamEffect` (0.5–0.6 s), `ServerBeamEffect` | 5 | **0.5–2.7 s** |

Those lifetimes are not decoration — they are the decode. **A countdown must reach zero when the
game stops listing the entity**, the entity list is in the same recording, so the check needs no
prior belief about the layout and no argument: it is the game settling it. `Report` runs exactly
that, and only over entities the capture saw both *arrive and leave* — one still listed on the
last frame has no expiry to check against, and counting it would let a value that merely drifts
down pass. The second signal is the same idea for space: **a position inside a component must be
near the entity's own**, which is why every observation carries `Render.CurrentWorldPosition`
alongside. Three floats in a plausible coordinate range prove nothing; three floats within a few
units of where the game already says the entity is cannot happen by chance — and a beam's *far*
end is then the interesting near-miss rather than a failure.

The read width comes from the schema's `PoolCell`, with the companion hunt's ladder behind it:
`DiesAfterTime`'s cell rests on seven gaps and `Beam`'s divides only 69 of 88, so a too-large read
falls back to a shorter one rather than recording nothing. The two that *are* well measured —
`LimitedLifespan` at 524 of 524 gaps, `GroundEffect` at 63 of 65 — are in the pool-cell audit;
the two thin ones are deliberately not, because they do not clear its bar.

`GroundEffect` is the one to record first if only one gets made: its carriers stand still for tens
of seconds, so a person can point at one and let it run out.

#### What the capture decoded

`tests/fixtures/session-2026-08-sweep.rec` — 3697 frames, six minutes, all four components in it.
Two decoded, two closed with a negative.

**`GroundEffect+0x58` is seconds remaining.** Not "a float that falls to zero" — an alpha does
that. It **predicts the delisting**: over 1445 readings on 54 effects, `now + value` names the
frame the game stops listing the entity with a median error of **−0.38 s** and a 5th–95th band of
−0.44 to −0.30. The bias is the finding, not the noise: **0.14 s of spread across 1445 readings**
means the value hits zero a consistent 0.38 s *before* the entity goes — the game keeps it for a
despawn beat. Nothing that merely decays holds a constant offset to a wall clock that tightly, and
nothing predicts a delisting from thirty seconds out.

**`Beam+0x58` and `+0x64` are the line it draws.** The near end is the beam entity's own position,
*exactly*, on 63 of 63 — the anchor that makes the rest readable, since it is a value this tool
already had from a different component. The far end is 17–1116 units away, median 400, and it is
**the one worth having**: what a player has to be out of.

The far end was confirmed by a control, not by plausibility. It lands within 30 units of an entity
the game is listing on **98%** of readings — and the **midpoint of the same line**, same beam, same
crowd, no reason to be anybody, manages **18%**. Landing near something is cheap in a fight;
landing near something five times more often than the middle of your own line is not. The
convergence says it twice: 60 distinct source points, 33 distinct targets, with three beams from
three positions ending on one point.

One control that did *not* discriminate, recorded so it is not mistaken for a second
confirmation: another beam's far end from the same frame also scores 97% — which follows from the
convergence rather than contradicting it.

**And the mistake that check nearly made.** The first version pooled the *beam carriers'* own
positions as the crowd, and scored the real finding at 69 of 1098. A beam ends on a monster, and a
monster carries none of the swept components — the question was being asked against the wrong
crowd. `SweepFrame` now carries **every** listed entity's position (free: the sweep already reads
each one on its way past), and the match happens **within the frame**, because a beam ends on
somebody standing there at that moment.

**`LimitedLifespan` and `DiesAfterTime` hold no timer**, and that is a result rather than a gap.
52,803 and 5,310 readings, 457 and 38 entities that expired inside the capture, and a timer was
asked for in all three forms it could take: time *remaining* (falls to zero at expiry), a
*deadline* (constant per entity, ordered like the death times), a *duration* (constant per entity,
ordered like the lifetimes). None answered. Everything that varies is set once and never moves,
and in both components the varying pair is one plausible pointer — the lifespan is presumably
behind it. Both stay fieldless in the schema, and a test pins that: a component called
`LimitedLifespan` that does not hold a lifespan is exactly what a later reader would assume had
merely not been looked at.

Two candidates are named in the schema without being fields, because a plausible shape is not a
measurement: `GroundEffect+0x38` is a float reading 18.66–18.67 with three distinct values across
72 effects — the shape a **radius** has, in the same units as `Render.CurrentWorldPosition`, to be
settled by standing at a measured distance and seeing where damage starts; `+0x48` is a small
integer with three distinct values, which is what a type or severity id looks like.

#### Both are drawn now

`WorldEntity` carries `GroundSeconds` and `Beam`; `GroundDangerLayer` rings every ground effect
with its countdown and `BeamLayer` draws each beam as the line it occupies. Both switches live on
the tracker's *Dangerous ground* tab and both default **off** — they draw over the fight, and an
upgrade must not change a screen nobody asked to change.

The reads are unconditional rather than behind a switch, decided on the measured counts: at most
**11 ground effects and 5 beams alive at once** across every committed recording, so it is one
4-byte read and one 24-byte read on a handful of entities, against the hundreds of monsters the
same loop already pays for. Gating that would cost more in state that can be wrong than it could
save.

**The ground rings are the same feature the rules were, done properly.** A `GroundDangerRule`
matches a metadata path somebody typed: it fires on whatever starts with that text, misses
anything nobody thought of, and can say nothing about the patch beyond "it matched". The new path
asks the entity whether it *carries a GroundEffect component* — the game's own answer — and reads
the countdown out of it. That is the shape of mistake this file already records paying for once,
a feature built on a person describing something the game names itself. The rules stay, because
they cover what the component does not: a Firewall or an ice crystal is a hostile effect wearing a
monster's components and carries no `GroundEffect`.

#### What the component means, and the wrong turn on the way to saying it

`GroundEffect` was first read as a hazard marker; then re-read as marking only a **decorative
decal**, nothing to do with damage. The second reading was the wrong one, and both of its supports
collapsed — which is the part worth keeping, because they collapsed for reasons this file already
contained.

1. A screenshot showed the ring on an **Abyssal Arsenal**. Its countdown read `0.0s` — and this
   very document records that a countdown sits at 0.0 for a measured **0.38 s after expiring**.
   That was a *spent* effect, not a harmless one.
2. **5880 of 5916** readings were attributed to a hideout. Those frames carry area level **0** and
   area hash **0** — a *loading* state in which the area **name** is still the previous area's.
   They were never hideout decorations.

Two mis-readings pointing the same way felt like corroboration. Neither was evidence, and the
lesson is the cheaper of the two: **check the state you are attributing to before you attribute
to it.** One glance at `AreaHash` would have stopped it.

**What the game's own data says.** `GroundEffectTypes` has **53 rows**, every one a real effect
kind applying a real buff — `IgnitedGround`, `ChilledGround`, `ShockedGround`, `CausticCloud` — and
no decorative row at all (row 26 is literally `Unused`). The rows the recordings show resolve to
**Spores**, **OrionMeteor** (*Desolation of the Awakener*), **CrownOfThorns** (*Sacred Ashes*) and
**Profane Ground**.

So carrying the component means the game considers the entity one of its ground-effect kinds. It
does **not** follow that every one damages the player — `Consecration` and `Haste` are in the same
table — so the *buff a row applies* is what decides, and that is now resolved rather than left open.

#### Which ground hurts, from the game's own words

`data/ground-effect-types.json` carries all 53 rows with a **harm** judgement and, where the buff
has one, the sentence the game shows while somebody stands in it. The split is **44 harmful, 6
helpful, 3 unclear**, and every row records *why* it was judged that way, because a classification
with no stated reason is an opinion.

The rule comes from `BuffDefinitions.BuffCategory`:

| category | meaning | example |
|---|---|---|
| 2 | debuff — every one describes damage, a slow or a drain | *"You are taking Physical and Fire Damage over time"* |
| 1 | grants something | Consecration, Haste, an oasis |
| 18 | invisible, no description — the **ailment** grounds | Ignited, Chilled, Shocked, Scorched, Brittle, Sapped, Withered |

Anything left over is **unclear**, not harmless: `Smoke`, the row literally called `Unused`, and
`Leyline`, which cuts both ways — more spell damage while it drains your Ward. Unclear rows are
drawn at **full strength**, and an unreadable word in the file falls to unclear too: the one wrong
answer worth engineering against is showing a hazard as safe.

The label leads with the game's own sentence, because no amount of reverse engineering was going to
produce a better one than the game was already going to show. Helpful ground is dimmed rather than
hidden — a screen where everything is equally red is a screen nobody reads.

#### Damaging ground feeds the dodge

The evasion planner scores eight directions by the **worst** distance to any danger and rolls only
when one beats standing still. Ground joins that scoring rather than getting a mechanism of its own,
and the join is one line of arithmetic: a patch contributes `distance − radius`, so **inside it
scores below zero**.

That is what makes "roll out of the fire" need no special case. A threat is a line and nothing is
ever *inside* one, so distance alone would rate the middle of a burning patch the same as its rim.
With the radius taken off, standing in one is strictly worse than standing anywhere outside it —
which is exactly the ordering `Escape.Best` already knew how to act on.

**Two switches, because they cost different things.**

| | default | what it does |
|---|---|---|
| *Never dodge into damaging ground* | **on** | only changes the direction of a roll that was already happening |
| *Roll out when I am standing in it* | **off** | presses the key with **no monster winding up** |

The first is free — nobody wants the tool to dodge *into* fire. The second is a new trigger in a
situation where the tool used to do nothing, so it is the user's call, and it still needs the act
gate on: pressing a key is that gate's business whatever prompted it. Its *rarity* floor is not
consulted, because a patch of fire has no rarity.

**Helpful ground is excluded, and that is the payoff of classifying the table.** Six of the 53 rows
grant something; rolling off a Consecration would be the tool actively making things worse.
Uncertainty goes the other way: an unclear row, or one no table can name, counts as harmful, because
leaving a neutral patch costs a roll charge and staying in a burning one costs life.

**Not filtered on `IsFriendly`**, deliberately — across both committed captures **not one** ground
effect carries that flag. Filtering on it would look like protection against rolling away from your
own ground and provide none.

**The radius is the guess this rests on**, and it is the same 20 world units the overlay's ring
draws, so what somebody sees is what the steering is reasoning about.

**The tests are synthetic, and the reason is measured**: the closest a ground effect ever comes to
the player is 38 world units in one capture and 44 in the other, so **nobody ever stands in one** in
the recorded material. A replay cannot exercise the case this exists for.

#### The size of a ground effect is still unknown

`+0x38` was the candidate and it is dead. Reading the **raw bytes** rather than the filtered value —
the check that should have come first — every ground entity in the map capture holds `0x4EC34228` =
**1.638 × 10⁹**, and every one in the sweep capture holds `0x41955DB2` = **18.6707497**. Constant
within a capture, different between them, and 1.6 billion is not a dimension under any reading. So
it is neither a per-effect radius nor a global one; it looks like a shared constant of another kind
— a time base, a seed, an id. `GroundEffectUseGameRadius` now defaults **off**.

**Somebody else hit the same wall**, which is worth recording because it is the only independent
data point this project has. A long-time PoE1 reverse engineer, asked about damaging ground, said
they could reach the effect from the components but *had trouble with the area of effect*, and
guessed that every entity is a square of always the same dimension. The constant-within-a-capture
behaviour is consistent with that guess; **two different constants are not**. Nothing found so far
carries the size, so the overlay draws a ring at a size the user picks.

**They must not both fire on one entity, and for a while they did.** The two passes walk the same
entity list and neither knew about the other. The shipped rule is spelled as the *exact* path that
carries a `GroundEffect` component — not a prefix that happens to cover it — so under **default
settings** every tagged patch was rung twice: once as a world-radius ring lying on the floor with
an X and a countdown, once as a flat pixel circle of whatever size the rule carried. Two circles of
different sizes on one patch of fire, which is precisely the "I cannot tell what these circles are"
the feature exists to end.

**A rule wins where somebody wrote one**, and the component pass keeps everything else — which is
nearly every ground effect, since a rule has to be written one path at a time. This was shipped the
other way round for exactly one commit, on the argument that the component reads a real value where
a rule carries a typed number.

The reason it settled here is not that the component knows less. It knows **more**: `+0x48` names
the exact kind of ground out of the game's own table, which no typed path can. It is that a rule is
an explicit instruction a person wrote down, with a colour and a size they chose, and a tool that
silently overrides one is a tool whose settings cannot be trusted. The split is by **authority**,
not by accuracy — and the rule still only *matches*; nothing rewrites it.

The decision lives on `TrackerSettings` rather than in the layer that draws, for a mundane but
binding reason: the test project runs on Linux and cannot reference the Windows-only overlay, so a
rule kept in the layer is a rule nothing can test.

#### Naming the ground, without needing the game installed

`GroundEffectTypeTable` resolves the row. It prefers the **install** — `Files/DatFile.cs` parses
`.dat` and `Files/BundleIndex.cs` opens the bundles, and the column list is vendored in
`data/ground-tables.json` with offsets *recomputed* from the widths, never stored. Confirmed on a
real install: `data/balance/groundeffecttypes.datc64`, **53 rows of 64 bytes** — and 64 is exactly
what the column list computes, which is the check that the layout is right.

It falls back to `data/ground-effect-types.json`, which vendors the 53 rows from the same DAT
export. That is what makes the feature work on a replay-only machine — every machine these tests
run on — and it is why the resolution is *tested* rather than only its failure modes. A row neither
source knows is reported as its number, never guessed.

The label leads with the **buff name**, not a buff count: counting discriminates nothing, since all
53 rows apply one, where the buff's name is the phrase on the player's own screen while they stand
in it — `CrownOfThorns — Sacred Ashes`.

`--groundtypes` prints the whole table and marks the rows the captures observed. It reads the
install (or the vendored copy), so it needs no running game and no fight to survive. Distinct from
`--glossary` and `--tables`, which read tables the game has already *loaded* off `FileRoot`: those
need a process but no area; this one needs neither.

**Why this is not a duplicate of `LoadedDatTables`.** The resident copy of a table carries the
fixed-size rows and **not** the variable-length section — and `Id` is a string, which lives in
exactly the half that is missing. That is why `DatFile` exists at all. The two do have a
cross-check to offer each other: the resident table knows its own row count and row size, so a
running game can confirm the 64 bytes without parsing a file.

**The beam is drawn in full, and that is the point.** A ring on the beam entity's own position
marks one end of a line up to 1116 world units long — worse than nothing, because it flags as
dangerous the one spot the player is already clear of. What is *not* drawn is a width: the one
thickness candidate in the component was exceeded by the beam's own length on two thirds of
readings, so a danger zone would be an invention, and the settings row says so next to the slider.

`HazardReadingTests` covers the half a decode usually dies in — that the values survive the trip
into a `WorldSnapshot` — by re-running the countdown's prediction check *through the snapshot*,
and by pinning that a pre-decode recording yields `null` rather than a zero that would ring the
whole screen.

#### Both are drawn the way the game draws them

The two shapes come from Path of Exile's own idiom, and they were built by rendering the geometry
to a file and looking at it before it shipped — twice each, because the first attempt was wrong
both times.

**What that render could and could not settle**, because it was trusted too far once. It settles
SHAPE: the chevrons being squat rather than dart-like, and the arms forming an X rather than a
plus, are both plainly visible in a picture and both were caught there. It does NOT settle
OPACITY, and the first version of the preview actively lied about it — PIL's `ImageDraw` writes a
colour and its alpha straight into the pixel instead of compositing it, so a 20%-alpha fill came
back as the full colour and the band's interior rendered near-white. That produced one wrong
conclusion (the ground ring's fill was dropped partly on the strength of it; the reference has no
fill either, so the decision survives on better evidence) and nearly produced a second. Alpha is
arithmetic and belongs in arithmetic: the beam's interior is `1 − (1 − 0.09)(1 − 0.18)` = **25%
opaque**, and at a fully opaque configured colour it would still be 28%.

**A ground effect is a ring on the floor with an X through it.** Not a screen-space circle: the
ring is a circle of world radius projected point by point, so it lies on the ground, foreshortens
as the ground does, and shrinks as the camera pulls back. Nothing is filled — the reference leaves
the interior clear and so does this, which is also what keeps a screen full of them readable.

The four quartering arms are picked **in screen space**, by taking the ring point nearest each
screen diagonal. Stepping four fixed world angles instead — the obvious way — produced a **plus**
rather than an X, because this game's isometric camera maps the world axes onto the screen
diagonals.

**A beam is a translucent band with chevrons marching along it**, the shape the game uses to point
somewhere. The chevrons carry the one thing a plain line cannot: which way it points. Their
proportions were measured off the game's own band — base about six tenths of the width, spacing a
little under one width, and *squat*, wider across than long. The first attempt had them longer
than wide and they read as darts strung on a wire.

**A third of ground effects carry no timer, and that is a fact about the game.** In the sweep
capture 33 of 72 effects held NaN in the countdown slot for their entire life and 39 held a real
number, with **no entity ever crossing between the two** — so an absent countdown means "this one
does not expire on a clock", not "the read failed". The ones without a timer are the long-lived
ones, 59–104 s against the timed ones' handful of seconds. The first version of the layer gated
the ring on the countdown and would have left a third of the burning ground unmarked; a test now
pins the split and the ring depends only on the component being present.

#### The radius candidate, and a better way to settle it

`GroundEffect+0x38` is in the schema now as **`RadiusCandidate`** — the name is the disclaimer,
because a field called `Radius` would assert something nothing has measured. The overlay sizes the
ring from it by default, and *that is the experiment*: a world-radius ring either hugs the burning
patch or it does not, and **one screenshot answers it**.

The earlier suggestion — walk towards the effect and note where damage starts — was a bad one and
is recorded as such in the schema. Ground effects expire, the scene they appear in is rarely
survivable to experiment in, the game does not permit that kind of pixel-precise movement, and the
result would have been a number nobody could report back. **Verify against something the game
draws, not against something it does to you.**

#### Picking a path instead of typing one

`GroundWatch` remembers every dangerous-looking path a session has met, keyed by path rather than
by entity, and the config window's *Dangerous ground* card offers them as a dropdown. It is the
same answer `BuffWatch` gives to the same problem — a rule matches an internal string written
nowhere a player can see — and the reasoning is stronger here: a ground effect is *gone* by the
time somebody has alt-tabbed to write a rule about it, so a list of what is there right now would
be empty exactly when it is wanted. It remembers for two hours, against the buff list's shorter
window, because league-mechanic ground is precisely what somebody sits down to write rules about
afterwards.

The row carries the column that decides whether a rule is worth writing at all: **whether the game
tags it**. A tagged path already has a component ring and needs no rule; an untagged one will never
be marked without one. `@nn` variant markers are trimmed when a path is seeded into a rule, as
`PreloadReader` already does elsewhere — a rule keeping the marker would match the one patch that
happened to be on screen when it was added.

**Every payload the page posts must be a JSON object.** The config host refuses anything else
before its dispatch switch is reached — a guard every other message satisfies without thinking,
because every other message sends a record. This card sent a bare array, and the result was the
worst shape a bug can take: nothing threw, nothing logged, the page rendered its own edit
optimistically for the length of its edit-hold, and the next poll re-rendered from a state that had
never changed. A newly added rule appeared and vanished a second later, which reads from the
outside as "this rule will not save". The rules now travel under a `rules` key, and a refused
request **names itself on the console** rather than being dropped in silence — the guard was right,
its quietness was not.

The honest regression test would drive the page against the real host, and cannot be written: the
config project is `net10.0-windows` and the suite runs on Linux. Parsing the JSON the page *ought*
to send would be worse than nothing — it would pass against whatever shape somebody believed in,
which is how the bug was written in the first place. So the test asserts the one thing that broke,
in the one file that broke it, and it was checked to fail against the old shape.

**What the picker cannot offer, measured rather than assumed.** The whole sweep capture yields four
rows, one of them tagged. Not one is a `GroundOnDeath` daemon — the burning, shocked and chilled
ground a rare monster leaves behind — even though that is the ground most worth a rule. Their paths
run through `Metadata/Monsters/MonsterMods/…`, `NoiseFilter`'s **Daemon** class matches
`monstermods`, and `WorldReader` drops them before a snapshot exists.

The consequence is bigger than an incomplete dropdown, and it is the part worth remembering: **a
`GroundDangerRule` written against such a path can never fire either**, whoever typed it and
however correct the text is, for as long as that filter class is on. The shipped default rule
(`Metadata/Effects/Spells/ground_effects/`) is not caught and does work. `GroundWatchTests` pins
both halves, and the card says so on the page — a limit invisible from the panel is one somebody
reads as a broken picker.

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
  - **"Costless for the other" is too strong, and a wave is the case that breaks it.** Named by
    the owner (2026-08-29) while deciding whether to build a per-animation danger table: a **wave
    rolling at the player** is a wide *front* — thin along its travel, long across it — and the
    segment the model draws runs along its direction of travel. So perpendicular moves *along*
    the front and does not leave it, while rolling forward *through* a thin front is the direction
    that plausibly works and scores zero, because it stays on the segment. The rule ranks the
    likely answer last. Not less precise on a third shape: **inverted on it.** And the shape is
    only half the problem — **a wave moves**, so whether a place is safe depends on *when* you
    arrive, and there is no time anywhere in the scoring. Two hazards with identical geometry
    need opposite answers depending on whether the front is advancing, which means the obvious
    next step — a table of shape and radius keyed on animation id — could not express a wave even
    if somebody wrote it; it would need a travel speed too. Which points at not writing a table
    at all — a front that can be *observed* needs no table and never goes stale, the same "ask the
    game rather than keep a list" move that answered the steering's hold, and `ProjectileWatch`
    already derives a direction and a speed from watching something move across successive reads.
    **But a hostile wave reaches none of that today**, and it is dropped three times over, each
    with a recorded reason: `ReadVisualEntities` is off, so the entity walk discards everything
    from id `0x40000000` up — decorations, effects, every projectile in flight — before the path
    is read (measured: 17 gameplay entities against 51 visuals per frame on a Spark session);
    `KeepEffects` is off, so a hostile thing that expires on its own and cannot be targeted is
    dropped as a ground effect wearing a monster's components (the rule that stopped flame walls
    being health-barred); and even with both on, a monster's projectile is usually filed under the
    *monster's* own path (`Metadata/Monsters/…/objects/LightningArrow`, 36 sightings in one
    recorded map), so it classifies as a Monster, carries no Life, and is dropped again — only
    `Metadata/Projectiles/…` becomes `EntityKind.Projectile`, which is mostly where a *player's*
    skills put theirs. So `ProjectileWatch` today follows your own projectiles, not a boss's wave.
    Both switches are live in the overlay (the **Projectiles** tab turns on the visuals, the
    **Effects** tab keeps the ground effects) and both now **persist**, which they had to before
    the question could be asked at all: nobody can watch an entity browser through a boss fight,
    so the answer comes from a `--record`ing — and a recording can only contain reads the running
    build performed, so a switch that forgot itself on exit could never be on when one started.
  - **Measured against a real boss** (`tests/fixtures/session-2026-08-effects.rec`,
    `HostileEffectTests`), and it **splits the question rather than answering it**.
    **Movement alone does not mean a hazard** — the first reading of this recording said it did.
    It reported `PermanentEffect` as a travelling threat on **1304 clean consecutive-frame
    steps, every one under 200 units and none over 1000**, and every one of those numbers is
    true. They belong to three effects **pinned to the player**: never more than thirty units
    away across 975 frames, having travelled the same ten thousand units the player did. A clean
    measurement was read as a travelling hazard without asking *what* was travelling, and what
    exposed it was the owner mentioning they had spent the recording running. **The
    discriminator is range, not movement.** What is left is short-lived: the boss's own effects
    run about **ten frames each**, half a second at the reader's rate, so an observation-based
    model gets roughly ten sightings to decide from — and one of them does close on the player
    monotonically (ten steps, none opening), which is one instance and not a rule. Ground effects
    move **exactly zero**, so standing and travelling danger *are* distinguishable.
    **But the path names nothing** —
    `Effect`, `PermanentEffect`, `SleepableEffect`, `BeamEffect` are engine words, not skill
    names — so geometry can be observed and *identity* cannot; a table may still be wanted for
    what a thing **is** while what it is **doing** comes from the world for free. And the third
    barrier is confirmed on data: **all 6864 sightings under `Metadata/Projectiles` in that
    fight are the player's own Spark**, not one monster projectile classified as a projectile.
    The wave itself is still unobserved — the boss is drawn at random and that fight had none;
    **the danger model is an open question and the scoring is unchanged.**
  - **Which way is "W"** comes from the game's own matrix, not from an isometric constant:
    project the player, step up the screen, invert onto the player's ground plane, and the
    difference is the world direction (`ScreenBasis`). Over the 1984 in-game frames of the
    monsters fixture that gives up-screen = world (0.7071, 0.7071) and right = (0.7071, −0.7071)
    — the world axes run diagonally across the screen — with the two screen axes coming back
    **0.19° from perpendicular in the world** at worst, which was not guaranteed and is what
    makes the diagonals evenly spread. The decisive test puts the derived direction back through
    the projection and asks whether it lands directly above the player on screen; a sign error, a
    swapped column or a row-major reading all fail it, and none of them fails a length check.
    That the game's forward key moves the character up the screen was the one thing no
    recording could answer — it is a fact about the *controls*, not about memory — and the owner
    settled it at the keyboard (2026-08-29), the same route by which the roll rule was
    established. **The whole sequence then held up over two complete maps** (2026-08-29): the roll
    goes the way the planner chose, WSAD can be held down throughout, and movement resumes in the
    held direction the instant the roll ends — no key to press again. That last part is what
    `PhysicalKeys` exists for, and the only way to confirm it was to play, because no recording
    can show a finger still on a key. Steering still ships off, on a different argument: taking
    the movement keys over should be something a person switches on deliberately.
  - **How long to hold is a number, and the attempt to make it not one is worth recording.**
    The steering key has to stay down across the frame in which the game resolves the roll's
    direction, and one frame is 16.7 ms at 60 fps, 33 at 30, 62 at 16 — so a single number is too
    long on a fast machine and, worse, **too short on a slow one, where it fails silently**: the
    roll goes where the player was already pointing, which looks exactly like the steering having
    chosen that direction. Two readings from play settle the range: **60 ms and 20 ms both work**,
    which is the shape the arithmetic predicts. The default is **60**, sized for the machine
    nobody has measured, because too-low fails silently while too-high only costs exposure — and
    the hold is the window in which the tool owns the movement keys.
  - **Reading the frame rate was looked at and is the wrong question.** Nothing supplies it —
    GameHelper2's FPS is its own overlay's `ImGui.GetIO().Framerate`, the AHK tool's is its own
    profiler's, and no reference reads one out of the game — but the deeper reason is that a
    frame rate is only a *proxy* for "has the game seen the keys yet". So `RollWatch` asked the
    game directly instead: wait for the player's own animation id to turn into one the game calls
    a dodge roll, because committing to the roll is when the direction is read, and hand the keys
    back the moment it does. `SteerHoldMs` became a ceiling. **The premise was right and the
    signal was not.** Measured in play with the ceiling raised to 200 so the reading could not be
    truncated, the line read **`4 rolls seen in 49-62 ms (middle 61)`** — tight, none on the
    ceiling. At 60 fps that is **three frames**, and the same machine had already shown a flat
    20 ms hold working, so the game reads the keys long before the animation id turns over. The
    id changing is a *downstream consequence* of the roll, not the moment the input was used. It
    landed at 55–62 ms, which is where the guessed 60 already was, so **it held three times longer
    than necessary and bought nothing — and was removed** rather than left as a switch nobody
    would use. What the episode leaves behind is the reading at the shipped ceiling,
    `32 rolls, 15 seen in 42-59 ms, 17 on the ceiling`, and the lesson in it: **more than half the
    samples sat at the bound, so the middle of 55 was never the real middle.** A measurement
    truncated by its own limit reports a number that looks like an answer.
  - **`Thread.Sleep` is quantised to the per-process system timer** — 15.6 ms unless something in
    *this* process has raised it, and since Windows 10 2004 the game raising its own does nothing
    for us. So every hold is a **floor rather than a duration**: a `Sleep(20)` lands somewhere
    between 20 and 31 ms. That is the safe direction to be wrong in, and it is worth knowing when
    reading the numbers above.
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

    **What the first real session answered — and it was not the question.** Six skills cast
    deliberately (`session-2026-08-skills.rec`); the winning chain was `wrapper+0x220+0x000`,
    and it names the **animation**, not the skill. The proof is the stride rather than the
    strings: the row address is `base + id * 106` exactly, over a span of 590 ids — a row array
    indexed by animation id — and a companion pointer at `0x228` names the file outright,
    `Data/Balance/Animation.dat`. Five of the six names match `data/animations.tsv` word for
    word, which is what gave it away; had they been skill ids they would not have.
    - **The sixth is the payoff, and then it got bigger.** The file said `InteractLeanWell` for
      animation 889; the game says `ElementalWeakness`. From six rows that looked like a table
      drifting a row at a time, and it was hand-patched as such — wrongly. Reading the **whole**
      table (`--animdump`) showed **three rows inserted** since the file was transcribed, at 584
      (`AbyssalLivingBomb`), 599 (`AbyssalPact`) and 904 (`RemidusDive`), shifting everything
      after them by one, two and three. Every one of the old file's 1084 rows fits one of those
      shifts **exactly, with zero leftovers** — so it was never a drifting table, it was a
      faithful table of an older patch. 889 was a symptom, and patching it made the file less
      consistent rather than more. Six samples can say that something is wrong and can never say
      what; only a whole-table read separates "a few bad rows" from "everything above 584 moved".
    - **What it cost while it stood:** 500 of 1084 ids named the wrong animation, 177 of them
      changing `AnimationKind`, and **37 classified quiet when the real animation is not** —
      `ElectricSpit` read as `DodgeRollSprint`, an empowered wyvern flame breath read as
      `FixedRunLayerBaseForward`. Those are threats the evasion filter dropped in silence.
      `IsQuiet` is asked the safe way round precisely so an *unknown* animation still counts; a
      confident **wrong** name walks straight past that guard. `data/animations.tsv` is now
      generated, and `AimTests` keeps the AHK tool's own live reading of id 872 as the outside
      check — the only one of its eight ids above an insertion point, off by exactly two.
    - **The name is not cosmetic**: `KindOf` classifies it, and that decides whether an animation
      is quiet enough to ignore. A wrong name is a mis-filtered threat; a missing one is a
      monster the tool can only report as a number.
    - **The filter earned its keep.** The same session offered `WandSpiritShield` for all eight
      animations and `Data/Balance/Animation.dat` for six — readable strings that name nothing,
      both correctly left unmarked, because a chain only counts when it gives a *different*
      name to every skill.
    - **Still open:** the skill id. Within 0x400 of the wrapper the only dat file referenced is
      Animation.dat, and two hops out of `CurrentSkillPtr` reached no text at all. The
      granted-skill route found nothing either — no offset in the first 0x100 of an entry holds
      the live animation id on exactly one entry, so the cast type is not an i32 there. And that
      session holds no frame with a wrapper but no animation, so whether a name is available at
      *commitment* is still unanswered.
    - One bug this shook out, of a kind this project has paid for before: the reader marked an
      animation id "resolved" *before* the read succeeded, so an id whose first sighting was
      unreadable was burned for the session — four of six skills learned, silently. Failures are
      now retried, bounded.

    **`--animdump` closes the loop on the shipped table.** The rows are an array, so ONE sighting
    addresses all of them: `base = row − id·106`. That turns `data/animations.tsv` from a
    hand-maintained list into something the game regenerates — run it after a patch, diff, done.
    The safety is entirely in the base, because a wrong row pointer still computes a base, every
    row still "reads", and the output would be a full table of confident nonsense committed over
    a working one. So **two *different* animations must agree** before it is used; the same id
    twice agrees by arithmetic rather than by evidence and is refused. It writes a new file beside
    the executable and prints the diff rather than replacing anything — re-extracting names that a
    dozen behaviours classify off is a deliberate act with a diff someone looked at, the same way
    an offset change is. It also samples both action slots, which answers in passing whether a
    MOVE wrapper carries the row pointer at the same offset as the SKILL wrapper it was found on.

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
- **Room names — the layout in words, from the same read that draws it as a shape.** The game
  builds an area out of named room files and writes that name on every tile the room covers, so
  the tile array already carries "exit_01", "overlay_bridge_03", "3open_01" long before anything
  is standing in them. The terrain layer draws the area's outline and cannot say what any part of
  it IS; this writes the name on it, and ctrl + clicking one pins it as an ordinary place —
  marker, label, A\* route, exactly as an exit gets. Four things worth recording.

  **Most of an area is scenery, and that is what decides which names are drawn.** The first run
  in a real zone made it obvious: labels everywhere outside the drawn outline —
  `Building_Fill_03`, `BuildingWall_Cv_06`, `TropicalCoast_Fill_01`. Nothing was wrong. The tile
  grid is a full rectangle and `GridWalkableData` is a *subset* of it, so the buildings you walk
  past, the sea beside them and the wall behind the fence are all tiles with names; the blue
  outline is only the walkable part of the same rectangle. A size threshold cannot separate the
  two, because a scenery block is large. Ground somebody can stand on can: each room counts how
  many of its tiles hold a walkable cell (`TerrainGrid.HasWalkableTile`, scanned per cell with an
  early exit — a byte holds two cells and a tile is 23 across, so every odd tile boundary lands
  mid-byte and a byte-wise scan would let a neighbour's edge cell answer for this tile), and a
  room with none is never named. **No opinion counts every tile as walkable rather than none** —
  a caller that cannot answer must get no filter, not an empty map.

  That first run also settled the projection for free, which is the kind of check this file keeps
  asking for: `TropicalCoast_*` sat over the beach and `Building*` over the houses. The labels
  land on the thing they name.

  **And it killed the size threshold as an idea.** The second run showed a cliff: at nine tiles
  the map was solid text, at ten there were four labels left. Nothing between the two pictures,
  because there is nothing between them — an area is built from ONE module repeated, so nearly
  every room is exactly nine tiles and a threshold in tiles is a step function at the module
  size. What survives at ten is only what the flood fill glued together, two adjacent placements
  of one file. Walkability did not save it either: `Building_Fill_*` went, but a `BuildingWall_*`
  room is a piece of level, not a mesh — it holds the wall *and* the ground in front of it, so it
  passes. What actually produces the soup is **repetition**, and the fix is the rule
  `TerrainLandmarks` already uses on tiles: a file placed more than four times in one area is a
  building block, not a place. `TerrainRoom.Placements` carries the count, and the rooms come out
  of the reader **rarest first, then largest** — because the second rule is that labels are
  packed against each other (`LabelPacking`): a name that would land on one already written is
  dropped, so the offer order decides which of two overlapping names survives. Together they make
  the density a function of the zoom rather than of a number somebody has to guess.

  A room is a **connected block of tiles sharing one file**, found by flood fill rather than by
  the pairwise clustering the boss arenas use. That is a cost decision and not a style one:
  clustering is quadratic in the tiles it is given, which is fine for the handful an arena name
  matches and not for an area's whole tile list; a fill over the grid is one pass whatever the
  area's size. What it cannot separate is two placements of the same file that touch — they come
  out as one room with twice the tiles, and the sub-ids that would tell them apart cost a second
  pass to buy back a merged label on repeated scenery.

  The rooms are found **whether or not anything draws them**, because the pass that reads tile
  paths for the landmarks already runs: they add an int per tile and a fill, once per area.
  Reading them on demand would mean the switch did nothing until the next zone.

  The centroid sits at the **centre of the block, not its corner** — a tile is 23 cells across,
  so anchoring on the mean tile index puts every name half a tile toward the map's origin, which
  is the offset the AHK tool shipped and had to correct. Worth knowing when comparing numbers
  against GameHelper2: its Radar reports a room's centroid on the corner convention, so its
  figure is 11.5 cells short of this one on both axes.

  And the **mouse**, which the overlay normally does not get: hovering is free, because
  ClickableTransparentOverlay reads the cursor with `GetCursorPos` rather than from window
  messages, so the position keeps arriving while the overlay is transparent to clicks. A click
  is not free — button presses come from messages — so it asks for the mouse for exactly as long
  as ctrl is held over a room, which is `WindowChrome`'s own trick applied to a map marker. Ctrl
  is what keeps dragging and zooming the map untouched.

  **And the readout answered the `.tdt` / `.arm` question: they are two LEVELS, not two
  spellings.** The game assembles an area from rooms — files under `Rooms/`, ending `.arm`,
  `overlay_bridge_03` and `exit_01` — and each room from tiles, files under `Tiles/`, ending
  `.tdt`, `BuildingWall_OceanEdge_CcMM_02`. `TileStruct.TgtFilePtr` is the TILE's file by
  definition, so what this draws is a materials list where the reference draws a floor plan.
  Both tooltips say so outright once you put them side by side: `.../Act2/2_8/Rooms/Overlays/…`
  over four-by-four tiles, `.../Maps/Port/Tiles/OceanEdge/…` over three-by-three.

  Where the room level lives is not known, and `RoomProbe` is how it gets found rather than
  guessed. Two places have room for an unaccounted pointer: the tile struct is 0x38 bytes with
  0x00, 0x08, 0x30 and 0x34–0x36 mapped, leaving **0x10–0x2F** — four slots; and the terrain
  struct has the tile vector at 0x28 and the grids at 0xD0/0xE8 with the span between them
  untouched. Under `--debug` the probe walks both, classifies every plausible pointer with
  `PointerPeek`, and follows anything structural ONE hop looking for wide text — because that is
  the shape the tile's own name has (a pointer to a struct whose `+0x08` is a `std::wstring`).
  A path under `Rooms/` or ending `.arm` is marked in the readout. Its real value is the
  recording: **a recording can only contain reads the running build performed**, so a question
  about bytes nothing reads was unanswerable offline — with the probe on, one session in one area
  captures the whole neighbourhood of both structures.

  It also cost a lesson worth keeping: the probe re-reads any text it finds instead of taking
  `PointerPeek`'s summary, because that summary is trimmed at sixty characters — and the paths
  being hunted run past it, so the extension falls off the end. A probe whose whole job is to
  recognise `.arm` cannot read a string that stops before it, and the failure would have been
  silent: the right answer on screen, unmarked.

  **What the first recording settled** (Gallows/Act2/2_5, 2026-09), and it is worth having in
  writing because two of the three answers close off an approach:

  - The rooms are in memory and they are the layout in words: 23 of them for that one zone —
    `Rooms/BonePassage/BonesEntrance_Cc_1.arm`, `Rooms/Fills/ritualsite_01.arm`,
    `Rooms/Unique/bonesouter_landmark.arm`.
  - **A room's name cannot be derived from its tiles.** Zero of those 23 share a stem with any
    `.tdt` in the recording, and the directories say why: rooms live under the AREA
    (`Gallows/Act2/2_5/Rooms/…`) while tiles live under the TILESET
    (`Desert/Badlands/…`), shared by every zone built from it. The cheap answer is dead.
  - **They arrive through the loaded-files table**, not through the terrain. Every one of the 42
    pointers to a room object sits at a multiple of 0x18 from the next — `FileRecordSlot.Size` —
    so what put them in the recording is the preload watcher walking the file table, and a room
    object has `TgtFile`'s own shape (`+0x08` is the `std::wstring`). That means the room NAMES
    of an area are already reachable today, stamped with the area-change counter; what is not
    there is where each one sits.
  - And the reason that recording could not settle where the room hangs off the terrain: **the
    tile array never lands in a recording.** The terrain pass reads it in one 340 KiB go and a
    recording drops any read over 64 KiB, so the file held exactly one tile out of 6075 — which
    cannot tell "no tile carries a room" from "no tile was looked at". Hence the probe's sixteen
    4 KiB windows: small enough to be kept, spread across the array, and the tiles it samples
    are drawn from them.

  **What the second recording settled**, with those windows in it — 2336 tiles across two areas,
  every slot, plus a hop: **no tile reaches a room**, by any slot, one hop out, or through the
  contents of the vector it carries. That closes the obvious place and, more usefully, it
  measured the rest of the tile struct, which had four unexplained slots: `+0x10`, `+0x18` and
  `+0x20` are **one inline `std::vector`** — begin, end, capacity — carried by 889 of the 2336
  tiles and all-zero in the other 1447; `+0x28` reads zero in every tile. The vector holds
  16-byte `{object, number}` elements, one to seven of them, and every object shares a single
  vtable. See `TileStruct` in the schema, which now records the census.

  **What the third recording settled, and it closes the terrain search.** With the probe opening
  inline vectors, the terrain struct's first 0x400 bytes map out completely — and hold no room:
  `+0x28` is the tile array, `+0x50` is 21648 bytes over an 87×81 area, which is exactly
  `(87+1)×(81+1)×3` and so is per tile CORNER, and `+0x68` is a vector of the area's ground-type
  files (`bone_fill.gt`, `waypoint_ground.gt`, …). Between the three recordings that is the tile
  ruled out on 2336 samples and the terrain struct ruled out on its whole head, with every
  pointer to a room object in all three sitting at a multiple of `0x18` — the file table, every
  time. **The game does not appear to keep the room→position mapping anywhere this tool can
  read.**

  Which leaves the files themselves, and the machinery for that already exists: the loaded-files
  table names the rooms of the current area, and `GameFiles` reads any path out of the game's
  bundles. If a `.arm` says which TILES it is built from, the layout can be recovered without a
  single new offset — rooms known, patterns known, tile grid already read. `RoomFiles` is the
  one question that decides it, and it reports rather than parses: nobody here has seen one of
  these files, and the count of `.tdt` mentions in it is the whole answer.

  Its first run in a real area produced "32 rooms, all binary, 0 mentions of `.tdt`" — **and
  that answer was the check, not the file.** Decoding UTF-16 as UTF-8 puts a NUL between every
  letter, which makes a text file read as binary and hides every string in it from a search for
  ASCII: exactly those two symptoms, for all 32 files, whatever they actually contain. It now
  counts both encodings, recognises UTF-16 as text, and prints the strings it finds — scanning
  both byte alignments, because a string inside a compiled file sits wherever the writer put it
  and an even-offset scan reports "no strings" for a file whose text starts on an odd one. The
  lesson is the one this file keeps relearning: **a check a wrong answer passes is worse than no
  check**, and it is worst when the wrong answer is the one that would close the question.

  Corrected, it answered: a room is **UTF-16 text** holding a grid of characters, a list of
  ground (`.gt`) and edge (`.et`) types, and `.ao` doodads with transform matrices — and NOT a
  single `.tdt`. So a room does not name its tiles, and the grid is the thing that could place
  one: the terrain struct's `+0x68` is that same list of `.gt` files for the whole area, and
  `GridLandscapeData` is a nibble per cell in the range 0–5. Whether a room's grid can be
  translated through its own `.gt` list into that nibble grid and searched for is the next
  question, and it is a question about the WHOLE file — the grid's dimensions, its alphabet, how
  a character maps to a type — which eight strings per room cannot answer. Hence "Write the
  Rooms Out" beside the readout: `RoomFiles.Dump` decodes every `.arm` of the area into
  `preloads/rooms-<area>.txt`, named to sit beside the `area-<area>.txt` the loaded-file list
  already writes. All of them rather than one, because the variation between rooms is itself the
  evidence — a field constant across thirty-two files is a header, and one that tracks the grid
  is a dimension.

  **The `.arm` format, read off 32 real files.** UTF-16 text: `version <n>`, a length-prefixed
  string table (the type files it uses, plus bare tags), a few header numbers, the room's own tag
  (`""`, `"end"`, `"Underground_NS_01"`), then `k <W> <H> <four side values> …` — the room's
  record — then those four side values again one per line, then the grid, one row per line, cells
  being `n`, `s`, `f <index into the string table>` or `k <24 numbers>`, then length-prefixed
  doodad placements. Two readings are settled rather than assumed: the four side values are
  **connections** (a `_Cnr_` room has two, an `_End_` room one), and a doodad's leading integers
  are **grid cells** — `853.462 / (250/23) = 78.52` against a written `78`, which is this
  project's own `GridToWorld` constant and nothing else.

  What it does NOT contain is a tile. Not one `.tdt` across all 32, in either encoding. A room
  names only its edge and ground types, and **the reference offers no way round that**: `.arm`
  appears nowhere in GameHelper2's `Radar.cs`, whose labels come from `TgtTilesLocations` — the
  `.tgt`/`.tdt` tile names this tool already draws. The `.arm` route is ours, so it has to carry
  itself. Which leaves one unread file type between here and a placement, so the dump follows the
  types the rooms declare and writes those out too: 32 rooms yield exactly 19 of them, and
  whether one lists its tiles decides the whole approach. Worth recording before it is answered:
  `bone_fill.gt` is declared by 14 of the 32 and `bones_edge.et` by 10, so even a yes gives
  room-FAMILY resolution — telling `AbyssTrail_Cnr_01` from `AbyssTrail_End_01` would then be a
  second step, off their grids and their connection counts.

  **And the types answered no as well, which is the useful part.** All 20 of them came back as
  tiny UTF-16 declarations naming no tile at all: a `.gt` is a NAMED GROUND TYPE
  (`bone_fill.gt` is 46 bytes reading `BoneUpperFill` and four flags) and an `.et` is the
  BOUNDARY between two of them, with a colour (`bones_abysswall.et` → `BonesAbyssWall #FFFFFFAA`,
  `bone_fill.gt` on one side and `bone_abyss.gt` on the other). So the chain room→type→tile does
  not exist, and the `.arm` route is closed for good — for the price of two clicks rather than
  an evening, which is what the dump was for.

  **What it opened instead is better than what it closed**, and three facts found separately met
  at it: the room probe had recorded `TerrainMetadata+0x68` as "a vector of the area's
  ground-type files"; the schema has carried `GridLandscapeData` since July as "static
  terrain-type **nibbles 0–5**", a value per cell; and the type dump showed a `.gt` is a name.
  Walking the vector's elements out of the third recording closed it: a Badlands area lists
  **six** — `bone_fill`, `trims1`, `bone_abyss`, `badlands_noburrow`, `waypoint_ground`,
  `badlands` — as eight-byte pointers 8 bytes apart, against the `0x30` stride every other `.gt`
  reference in memory sits on, each pointing at a file object whose path is at `+0x08` like a
  tile's `TgtFilePtr`. Six files against nibbles 0–5, arrived at from opposite ends. **A nibble
  is an index into that list**, so the map can say what the ground IS — the abyss, the fill, the
  waypoint — instead of naming the tile template that happens to draw it.

  `TerrainGroundTypes` reads it and **refuses to be believed on its own say-so**, because a
  wrong offset here would draw a plausible map of nonsense and nothing about it would look
  wrong. Three gates, in order: the landscape buffer must be exactly as long as the walkable one
  before a single nibble is read out of it with the walkable grid's packing (equal length is
  what licenses the reinterpretation; a different length means something else is being read);
  every nibble must index a file the area actually lists; and the types must **separate on
  walkability** — an abyss walkable nowhere, a fill walkable nearly everywhere. That last one is
  the gate with teeth, because a mis-read grid samples the same ground for every type and lands
  them all on the area's average. `GroundRegions` is empty unless all three pass, so the refusal
  lives at the source rather than in a flag a layer could forget to test.

  The regions themselves come from `TerrainRooms.Find` unchanged — contiguous tiles sharing a
  name is the same question the rooms ask, and it wants the same answer: a centroid to put a
  label at, a size to drop the slivers by, rarest-first ordering for the packer.

  **And then the first real area showed the gap in it.** The Titan Grotto drew nothing and said
  nothing — an empty map with no explanation, which is precisely the confusion those three gates
  exist to prevent, reintroduced one level ABOVE them. Two mistakes, and the second is the one
  worth remembering. `OverlayLayout.Hint` is a **hover tooltip**, so the sentence explaining an
  empty map was itself invisible unless somebody happened to point at the right control; that is
  a five-minute fix. The real one: the reader gave up in four places with a bare `null`, so
  `Ground` being null carried no reason at all — the checks were carefully designed to explain
  themselves and the code that runs BEFORE them was not. Every refusal now names itself with its
  numbers ("landscape 44160 bytes against walkable 42320 — not the same grid", "element 3 of 6
  names no file"), the note lives on `TerrainGrid` rather than on the ground it may not have,
  and it is written out on the switch panel and into `Describe()`. The lesson generalises past
  this feature: **a diagnostic that only covers the interesting failures is not a diagnostic**,
  because the boring ones are what actually happen.

  **And the note it then printed named a mistake in the evidence, not in the code.** "no
  ground-type files at +0x68 (element 0 of 5 is not a pointer)". Going back to the recording with
  the terrain struct found by its OWN fields — tile counts at `+0x18`/`+0x20` agreeing with a
  `0x38`-stride tile vector at `+0x28` — settles it: `+0x68` really is the list, in both areas
  the recording holds, and **its first element is null**.

  ```
  +0x68  VECTOR 56 bytes  [0, ptr, ptr, ptr, ptr, ptr, ptr]
                           ↑ blank   bone_fill … badlands
  ```

  A nibble of zero means **no ground type here** — the void around the playable area — and the
  slot is a position in the list rather than a hole in it. The reader's "every element or none"
  rule threw six good names away over one deliberate blank. Worth being precise about how that
  got shipped: the six `.gt` pointers were found in the recording as a contiguous run 8 bytes
  apart, and an older probe note said `+0x68` is "a vector of the area's ground-type files".
  Those were two findings, joined by assumption — **the search for a vector header matching the
  run came back EMPTY and that was noted and then written up as though it had matched.** The run
  began at `+8` precisely because a null is not a pointer to a string.

  Fixing it exposed a second thing, which is the more interesting one: **the blank would have
  gutted the walkability check.** It covers the void, so it is walkable nowhere, so it satisfies
  the "mostly not walkable" half for free — leaving a gate that only asks whether ANY type is
  walkable. Half a check that always passes is most of the way to no check, so unnamed slots are
  excluded from the spread, and a test puts the blank on one side of the map and a named,
  fully-walkable type on the other to prove the gate still says no.

  **Then the gate did its job against the theory it was built for.** The Titan Grotto, list read
  correctly this time: *"9190252 cells name a type beyond the 5 the area lists — the list and the
  grid do not belong together"*. Not a rounding error, most of the grid. **A landscape nibble is
  not an index into `GroundTypeFiles`**, and the connection the whole ground-type layer rests on
  does not hold. Which is the check earning its keep twice over — the alternative was a map
  labelled confidently and wrongly.

  What survives is worth keeping separate from what does not. `GroundTypeFiles` at `+0x68` IS
  the area's `.gt` list, measured in two areas with readable names; its length varies per area
  (7, 6, 5); the two grids are the same length; and the blank first slot is real. What is dead
  is only the pairing. Note the shape of the disagreement: the list length varies per area while
  the schema's July note says the nibbles run 0–5, which is what a FIXED terrain classification
  looks like rather than a per-area index — but one number cannot settle that, so the verdict now
  carries a histogram: every nibble value that occurs, how much ground it covers, and how much of
  that is walkable. The walkable share is what gives an unnamed value meaning at all (whatever is
  never walkable is the void or the abyss), and it is shown only when the reading failed, which
  is the only time it is worth the space.

  Finding the inline vector cost the probe a correction worth keeping: **it had been peeking
  `+0x10` as a pointer.** Reading a vector that is laid out as FIELDS rather than pointed at classifies
  whatever its elements happen to begin with, and never opens the array — so a whole level of
  the struct was invisible in a probe designed to find exactly that kind of thing. It now tests
  three consecutive slots for begin/end/capacity, and does so as an ADDITION to the ordinary
  walk: three references in the right order satisfy that test by luck, and skipping the next two
  slots on the guess would hide whatever they really are.
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
  - **Except the atlas, which is a panel the tool WORKS ON.** The rule above holds for a panel
    this tool has nothing to say about. On the atlas it names the maps, draws the routes, plans
    the rituals and has a tab for each — so hiding every window the moment the atlas opened made
    them disappear exactly when they became useful. Worse, it took the interface browser with
    it, and that is a circle: the browser exists to walk the game's UI tree, most of what is
    worth walking is inside a panel, and a browser that hides itself whenever a panel opens can
    never be pointed at one. `PanelArea.WorkedOn` names the exception, `HidesWindows` asks it,
    and the status readout lists only the panels that actually take a window with them — a line
    reporting a hiding that is not happening is the same lie in the other direction. The rule
    lives on `PanelArea` rather than in `WindowChrome` so it can be tested at all: the window
    chrome is Windows-only and the test project is not.

    Whether the atlas SKILL panel belongs with it is deliberately left open. It looks like a
    column down one side, which would make it another case of hiding from something the atlas
    tabs are for — but which panel of the interface it actually is has not been established, and
    that is not a thing to guess at.
- **Getting out of the way of the game's own HUD, which is a different problem.** A panel is
  open or shut and the answer is to stop drawing; the HUD is ALWAYS there and the answer cannot
  be. The large map is the case: the game draws it across the whole window — its UI element says
  so, and the element is right — and then paints the orbs, the flask and skill bars and the
  experience strip on top of it. Everything this tool projects onto that map therefore landed on
  the interface too, and a terrain outline over the life orb hides the one number a player is
  watching. An overlay sits above the game and cannot be painted under anything, so the only way
  to be underneath the HUD is to not be there.
  - **A region, not a rectangle.** "Everywhere except those four places" is not a rectangle, so
    `ScreenRegion` is a rectangle with holes and a partition of what is left. Point markers ask
    `MapView.Contains`, which every one of them already did; the layers drawing CONTINUOUS
    geometry — the terrain quad, a route line — draw once per piece, because ImGui clips to one
    rectangle at a time. The pieces do not overlap, so nothing is drawn twice, and a route
    crossing the HUD is CUT at its edge rather than dropped whole.
  - **The HUD is MEASURED, and the first attempt at this got that wrong.** It shipped as boxes
    the user drags over their own orbs, with a guessed band across the bottom as the default,
    on the belief that nothing in memory named the pieces of the interface — `ImportantUiElements`
    carries the panels and the maps and stops, and the reference has no answer either
    (GameHelper2's Radar has the user drag a "culling window" once and remembers it). That
    belief was wrong, and this project's own interface browser is what disproved it in a single
    screenshot: the HUD is one UiElement with StringId `"HUD"` among the UI root's own children,
    and its parts are its children — `experience_bar`, `life_orb`, `mana_orb`, `magma_mana_orb`,
    `botleft_buttons_layout`, `HUDLeft`, `HUDRight`, the orb frames — each carrying its own
    position and size like anything else in the tree. `InterfaceReader` reads them every tick, so the
    region holds at any resolution and any interface scale, with nothing eyeballed. **The tool
    had already built the thing that answers this question and the question was asked without
    using it.** That is the CLAUDE.md rule ("never guess — read the reference") failing against
    the project's own code rather than against somebody else's.
  - **Found by its id, not its index.** The HUD sits at child 97 of the root in this build, and
    a position in a list of 156 siblings is the most fragile thing this could depend on: one
    element inserted above it moves everything below, the wrong element is measured, and the map
    is kept off a rectangle that is not there — silently, because a rectangle is a rectangle.
    The index is a first guess, verified against the id; a miss falls back to a scan of the
    root's children. The address is cached and re-checked each frame (it must still answer to
    `"HUD"`), so an area change or a patch costs one scan rather than a wrong answer.
  - **The maps are excluded by address.** Whatever the tree turns out to look like, an element
    the minimap lives under must never come back as a piece of interface — that would take the
    minimap out of the region it is meant to be drawn ON, and the radar would stop working while
    every readout showed a healthy HUD. Their ancestors are walked once and skipped by address,
    at both levels the measurement descends.
  - **The atlas has the same problem and needs a different list.** Its panel is drawn across the
    whole window too, with the orbs, an open inventory and an atlas skill panel painted over it,
    so the web, the routes and the labels landed on all of them. It keeps off the same measured
    HUD, but two things differ. It may not keep off the ATLAS panel — that is what it draws on,
    and the map's list would leave it nowhere to go. And a panel BESIDE it is taken at its
    measured size rather than at the whole screen: `PanelReader` reports the panning kinds as
    screen-filling on purpose, because over-answering only costs a hidden window there, while
    here it would erase the feature. So `PanelArea` carries both answers — the conservative
    rectangle for windows, the reading for this — and the atlas skips a panel that could not be
    measured, failing towards drawing.
  - **Lines are CUT, text is all-or-nothing.** On the atlas the lines are the content: a
    connection dropped because it clips the corner of a panel is a route that silently is not
    there. So each segment is cut against the keep-out rectangles in its own parameter space
    (Liang-Barsky, then the gaps between the blocked intervals) — a segment touching nothing
    costs four comparisons per rectangle and comes back whole, which is nearly all of them.
    Redrawing per free piece was the alternative and is not affordable at a couple of thousand
    connections. Text goes the other way: half a name plate cut off by a clip rectangle is a
    word broken mid-letter, so a plate that overlaps anything is not drawn at all, and the
    contents under a skipped name keep their positions rather than shifting up into it.
  - **The atlas screen has furniture of its own, and it is measured the same way.** The world
    screen the atlas is a page of paints a title bar with the act tabs, a search box, a quest
    selector, a map legend and a pin editor over the top of it — the same relationship the HUD
    has to the large map, one screen along. Those are ordinary elements too, so
    `InterfaceReader.AtlasChrome` reads them: it walks UP from the atlas panel to the ancestor
    sitting directly under the interface root, and measures that screen's other visible
    children. **Under the root the caller was given**, not the top of the tree — the
    interface root is itself the real UI root's main child, so counting back from the end of
    the chain lands above the screen, whose siblings are every panel in the game. That
    shipped once and turned the whole atlas overlay off: nothing drawn, while the Atlas tab
    reported the read as perfectly healthy. A synthetic fixture cannot catch it either,
    because a test tree's root has no parent — the regression test gives it one. Found by where the
    atlas IS rather than by name or index, and the atlas's whole ancestry is excluded by address
    — otherwise the page the atlas hangs in is a sibling of the furniture, and taking it would
    blank the overlay while every measurement in the readout looked healthy. Only the VISIBLE
    ones, which is not a detail: `fade_to_black`, `vignette` and `consume_input_frame` are the
    size of the screen and usually idle — and their own flag is not enough to tell, so a part
    whose rectangle covers the WHOLE screen is dropped on its own. That is the difference
    between the region degrading and collapsing: honouring such a part is not "keep off
    that bit" but "keep off everything", identical to the feature being switched off and
    reached without anybody switching it off. Exactly the whole screen, never a share — a
    part that over-claims by half stays honoured, listed with its rectangle, one click from
    off. The `--debug` readout names whichever parts were dropped this way, because
    finding out which one it was otherwise costs a round of screenshots. And a region with
    nothing left in it is taken as
    evidence that the keep-out is wrong rather than that the atlas is covered: the panel is
    drawn across the whole window, so the overlay falls back to drawing everywhere — the
    same fail-towards-drawing rule every unreadable answer here gets.
  - **The cap on keep-outs is reported when it bites.** It was sixteen, which was far past what
    the HUD alone has parts — and then the atlas arrived with three sources at once (the HUD, the
    world screen's furniture, the panels beside it) and the honest count went to nearly thirty.
    The tail of that list was dropped silently, so the bookmarks panel was drawn over while every
    part ahead of it worked: a symptom that reads as a measurement problem and is not one. The cap
    is now 64, and `ScreenRegion.Refused` counts what it turned away so the readout can say
    `(N REFUSED - past the cap)` instead of leaving it to be found by screenshot. A rectangle
    with no area is not a refusal — an off-screen panel must not make that line cry wolf.
  - **A part can be switched off by name, and that is all the setting there is.** Some of these
    parts are containers, and a container reporting a rectangle far larger than what it draws
    would quietly eat the map — the atlas panel has form here, stating an extent 733 pixels
    narrower than the screen it covers. Every measured part is listed with the number it
    produced and how that number was arrived at (its own extent, or what its children cover), so
    an over-claiming part names itself instead of looking like a broken projection. The
    hand-dragged boxes survive as an EXTRA, empty by default, for what measurement cannot reach:
    another overlay parked over the game, a widget, a part whose element understates itself.
  - **Open panels are added to the same region, measured.** Those the tool CAN measure, so they
    are not guessed at: the rectangles `PanelReader` already read this frame go in beside the
    zones. That happens whatever "hide behind big panels" says — turning that off means "do not
    blank the whole overlay", not "paint the level layout across my stash".

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
- **And getting out of the way is now a rectangle, not a blackout.** Hovering a map used to hide
  the entire atlas overlay — every label, every route, every line, across the whole screen — which
  was the only answer available while the overlay could not keep off part of the screen. It can
  now, so the panel the game puts up gets the same treatment as the orbs and the title bar: it is
  measured, the region is carved, and the drawing goes round it. `AtlasHoverPanel` finds it BY
  APPEARING rather than by name, because **the game does not name it**: read out of the interface
  browser with a map hovered, it is the element at `[22][17][1]` — a grandchild of the world
  screen, 17 children of its own, 658×194 — with an EMPTY StringId, hanging in a nameless anchor
  that carries the position it pops up at (the panel itself sits at relative 0,0 inside it). So
  there is nothing to match on, and matching `[22][17]` instead would be an index into a list a
  patch reorders, failing exactly as silently as a wrong name: the overlay draws across the panel
  while the readout calls the keep-out healthy. What there is every tick is the measurement — the
  parts on screen with nothing hovered are a free baseline, and a part that was not there, or was
  not there *at that rectangle*, is the panel the game just put up. Both halves matter because of
  how the anchor measures: it claims no extent of its own, so `InterfaceReader` falls to the
  bounds of its visible children — nothing while no panel is up, the panel's own rectangle while
  one is, somewhere new each time. That also makes the one-level-down rule in `Measure`
  load-bearing rather than a nicety. The rectangle needs no extra work — the
  panel is an ordinary interface part and was already in the keep-out list — so finding it only
  answers whether the fallback is needed, and the `--debug` row NAMES the part, which is how that
  StringId finally gets read. Three things make the fallback the safe direction rather than a
  regression: a hover whose panel is not among the kept-off parts hides exactly as before, a
  baseline taken on another screen is thrown away (opening the atlas with the cursor already on a
  map hides until the cursor leaves a node once), and a part somebody switched off in the
  keep-out editor does not count as found — it is not covering anything as far as the drawing is
  concerned.
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

### The tables — a dat table is a loaded-file record

Rows of the game's static tables have been readable for months, but only **sideways**. A
MinimapIcon component holds a row, an NPC holds a row, a chest holds a row — so the tables this
tool could read were exactly the tables something on screen happened to point at. A table that
nothing points at was not far away, it was unreachable.

It stopped being unreachable when the two things turned out to be one thing. The game already
keeps an index of every file it has loaded, hanging off the `FileRoot` static — that is what
the preload alerts walk — and **a `.dat` record in it is the table object itself**, row store
and all. `FileRecord.Name` and `DatTable.Path` are not the same shape at the same offset, they
are the same field.

`session-2026-08-sweep.rec` says so from both directions at once. The object an NPCs row calls
"the table" (`0x31826080AD0`, path `Data/Balance/QuestFlags.dat`) is also one of the 8,406
records the `FileRoot` walk enumerates — and it is not one coincidence: of the four tables that
recording holds a foreign pointer to, *every one* is a record in the file table.

So the route to any loaded table is:

```
FileRoot -> LoadedFilesRoot -> bucket -> FileRecordSlot.Record -> DatTable -> RowStorePtr -> rows
```

`LoadedDatTables` walks it. **What decides which records are tables is not the name**: matching
`.dat` on a path would cost a string read for all eight thousand records to find ninety, and
would still believe a file named like a table but not parsed as one. `PointerPeek.DescribeTable`
asks the structure instead — a row store, two containers that divide exactly, and a by-Id index
whose first entry points at the first row — and reads the path only once a record has passed.
For nearly every record that is a single read that fails.

The first thing read through it is **`KeywordPopups`, the game's glossary**: the table behind
the `[Key|Text]` markup its own skill and mod texts are written in. Rendering that markup needs
no table at all — it carries both halves, so `KeywordGlossary.Plain` is static — but the popup
behind a highlighted word lives nowhere else. The row was identified from a dissector screenshot
two ways that cannot both be wrong (nine rows on a 0x48 grid; the only PoE2 table of 1,275 whose
0x48 row starts with five string columns), and the reader **refuses a table that does not report
0x48**, because a dat table divides its own rows by its own by-Id index and so answers its row
size without any schema at all.

**It runs.** `session-2026-08-glossary.rec` is `--glossary --record` on a live client, and
`GlossaryTableTests` replays it: 6,907 loaded files, 131 dat tables among them, KeywordPopups
found by name, and 1,026 rows of 0x48 read as English. That closes the one hop no other capture
could reach — every other recording walks the file table for its *names*, and the preload reader
stops one byte short of `RowStorePtr` at `+0x28`. It also turns the 0x48 from column arithmetic
over dat-schema into the client's own number.

**And it corrected the tool three times, all in the same direction.** The screenshot showed
`+100%%`, so `Plain` collapsed a doubled percent — the raw column holds `+100%`, and the
doubling was `ImGuiText.Escape` in our own drawing path. A bracket with no pipe was written down
as a form "not seen in this table"; there are 913 of them. And the definition cap of 512
characters silently truncated 55 rows, `Critical`'s ending mid-word. Every one of the three came
from reading the tool's own rendering as if it were the game's data — which is the failure this
document's first rule is about, arriving in the one place that felt safe.

**A cap that truncates is not a cosmetic bug**, and the second capture is what shows it: read at
512, the table appears to refer to 317 keywords; read whole, it refers to 328. Eleven references
lived in the tails that were being cut, so the feature was losing exactly the thing it exists to
provide, and no test could see it — a recording holds only what was read, so the capture taken
with the short cap could never have disagreed. The committed fixture is now the 2048 one, and
the maximum is measured rather than assumed: `Flammability`, 1429 characters, with nothing
coming back at the cap.

### What the same walk says about the column widths

Every dat-row offset in this project is arithmetic over a table of column widths, and that
arithmetic had been confirmed on **three** tables — by harvesting rows out of a recording and
checking their stride by hand. A loaded table reports its **own** row size, so one walk asks all
of them at once, and `--tables` prints the answers.

Over those captures: **all 134 tables report exactly what the widths compute**, from
`ModFamily`'s 8 bytes to `Mods`' 677. A width that is wrong for a common type could not survive
that — one byte off on `bool` would move about ninety of these.

**It first read 123 of 126, and both gaps were in the asking, not in the game.** This document
said "three tables disagree, all with the game bigger, which is what a reference lagging a patch
looks like" and "five tables aren't in dat-schema at all". Neither was true.

- **The interval rule.** An interval column is two values, so it costs twice its type — and
  `Mods.Stat1Value…Stat8Value` are a modifier's minimum and maximum roll. Eight of them at +4 is
  the 32 bytes `Mods` was short; `AlternateTreeVersions`' three are its 12. A hypothesis that
  explains the *sign* of every discrepancy is not thereby right: "the reference is behind" and
  "our arithmetic is incomplete" both make the game bigger, and only one was testable from here.
- **Capitalisation.** The game's path and dat-schema's table name disagree on case, so an
  exact-match lookup reports a table as *unknown* rather than as different: the game loads
  `AtlasPassiveSkillSubtrees`, the schema says `AtlasPassiveSkillSubTrees`, and likewise
  `MTXTypes`/`MtxTypes`. All five "missing" tables agree on their row size to the byte once
  matched case-insensitively.

Nothing shipped was ever wrong — none of the seven tables this project computes offsets in has an
interval column, so the bug was in the checking. `dat-offsets.ps1` now doubles interval columns.

- **A missing trailing column.** `EndgameMaps` computed 0xEF where the game says 0xF0, with no
  interval column in it — and a third source settles it: repoe-fork/dat-export's *heuristic*
  PoE2 schema, derived from the data itself, lists 28 columns to poe-tool-dev's 27, the extra
  being a `bool` at the very end, and totals 0xF0 to the byte.

**134 of 134 explained**, with nothing left attributed to "the reference is behind" that wasn't
then found in a reference. Three sources, and they disagree, so it's worth knowing what each is
for: poe-tool-dev/dat-schema is what `dat-offsets.ps1` downloads — one file, every table, and it
agreed with the game on 133 of 134; repoe-fork/dat-export publishes a schema derived from the
data, which is why it caught a column nobody had named — check it when a table disagrees with the
game; jchantrell/dat-schema was the least complete of the three here. **The game is the arbiter**:
it reports its own row size, and `--tables` prints it.

**And the route's limit, which is a fraction rather than a fact about any one table.** The walk
found 134 tables among 6,914 files, and **157 of those records are `.dat` files at all — against
about 1,020 PoE2 tables** in the community schemas. Fifteen per cent.

`WorldAreas`, `MinimapIcons`, `NPCs` and `ItemVisualIdentity` — the four this project reads rows
from — are in none of the 6,913 record names `--tables` reads, in any spelling, while `Stats`,
`Mods`, `BaseItemTypes` and `QuestFlags` are. **Confirmed from the row side too**, so it isn't a
name that failed to read: the same capture holds MinimapIcons *rows* — `Waypoint`, `StashPlayer`,
`MapDevice`, 159 and 154 row-widths apart on the 0x26 grid — and no record the walk accepts
brackets them. The table is in memory and out of the walk's reach at once.

**What that split is not is a story about which tables get loaded.** All eight are core tables the
client can't start without — which is exactly why the QuestFlags hunt never had trouble reading
its table. An earlier draft here called the four absences a coin flip at 15% coverage; that was
worse than wrong, because the four *present* ones were picked because they were visible in the
listing. A sample chosen after the fact says nothing.

**Two explanations stood, and one is now out.** Either the table tracks only what the resource
loader pulls in — it is mostly art, 1,219 `.tok` and 903 `.ao` to its 157 `.dat` — or our walk was
seeing a slice of a larger table.

This document argued for the slice, twice and wrongly. First on a fact: it said `BucketCount` is
`0x10` "because GameHelper2 says so, and GameHelper2 is a PoE1 tool". **GameHelper2 is a PoE2
tool** — its `GameOffsets/GameProcessName.cs` maps every process name it knows to "Path of Exile
2", and this document's own first rule calls it "a working tool against the same game". So
`TotalCount = 0x10` was a PoE2 number all along, not a PoE1 one carried over. Then on the
reasoning: with that invented doubt in hand, the slice became "the more economical reading" — it
was economical with a fact nobody had checked.

**The probe has run.** `PreloadReader.BucketsBeyondTheCount` on a live client, 2026-09-01: nothing
past the last bucket looks like one, and `--tables` says so on every run. Sixteen buckets is the
whole table, and coverage is not why anything is missing from it.

What's left is the resource-loader explanation — and with it the question of **why four core
tables travel through the loader and four don't**, which is now the thing to answer rather than a
rival to weigh. Either way, don't plan on finding a particular table this way: the row-pointer
route through a component is the only route to one the walk doesn't turn up.

And being on it is not being parsed: 23 of those 157 have nothing usable at `RowStorePtr`,
`GrantedEffectsPerLevel` and `Languages` among them. A table can be present and rowless, which
is why `Survey` reports the two states separately.

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
