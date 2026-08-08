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

## Building and running

```bash
dotnet build                                   # whole solution, any OS
dotnet test                                    # Core tests, any OS

# Windows, game running (elevated):
PoEformance.App                                # attach + drift report
PoEformance.App --overlay                      # the in-game overlay
PoEformance.App --record session.rec           # same, capturing everything
PoEformance.App --replay session.rec           # rerun against the capture, no game needed
```

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

- **Read cost over time.** A live number answers "is it slow now", which you can already see.
  The useful questions need the shape over a whole map, per phase — one graph for the total
  says a frame was expensive and nothing about why.
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

- **Health bars, and looted chests.** Both came out of reads that were already happening or
  already described: the corpse check read a monster's current health and threw the maximum
  away, and the Chest offsets sat in the schema unread. The interesting part is how they FAIL
  - an absent component is not "dead" and not "opened", and getting that backwards makes a
  whole feature disappear the moment an offset drifts, with no error to trace.
- **Routes that stay on one storey.** The walkable grid is flat: a bridge and the ground under
  it are the same cells and both walkable, so a two-dimensional search draws a route through
  the floor. Only the height separates them.

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
path, the flag fingerprints seen, how many children read as maps, and the first few decoded. It
is the first thing to press when the atlas is open and nothing is drawn on it.

When the path finds nothing, it goes further and **hunts for the panel by shape**: hundreds of
children mostly sharing one flags word is what a grid of map nodes is and what almost nothing
else in the interface is. It prints the matching child paths, marks the one whose fingerprint it
recognises, and the answer is pasted into the schema — which hot-reloads, so a drift that would
otherwise be a debugging session is an edit. This is the `drift scanner` idea from the build
order above, applied to the one feature whose offsets are entirely unverified. The ritual
offsets are worse still — they hang off that same panel, so a wrong panel path makes every one
of them read rubbish. Check that the atlas draws at all before believing anything the ritual
window says.

### Remaining

More features on the slice, and configuring them from the page.

Two whole areas of the reference tool are still absent, and both are blocked on the same
missing piece — an inventory reader: loot tracking with prices, and moving items into a
container. Neither can be built without the game running to verify the reads against.
