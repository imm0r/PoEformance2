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
- **Oodle**, via OozSharp — a managed decoder, so there is no licensed library to ship.

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

### Remaining

More features on the slice, and configuring them from the page.

Loot tracking with prices and moving items into a container both now have the reader they were
blocked on; what they still need is the game running to verify it against.

The packed-file reader needs the same: the formats are pinned against built fixtures, but
whether PoE2's own bundles decompress, and what its textures are actually block-compressed
with, only a real install can answer.
