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

## Threading model (planned shape)

One **reader thread** produces immutable snapshots at its own pace; publishing a
snapshot is a single atomic reference swap. The **render thread** (ImGui) reads
whichever snapshot is current — always internally consistent, never locked. Heavy
computation (pathfinding, pricing) gets explicit worker threads that read snapshots
and publish results the same way.

No shared mutable state, no locks in the hot path, no async in the read loop. The
AHK tool needed two extra *processes*, shared memory and a seqlock protocol to
escape its single thread; here the same headroom is a `Thread` and an
`Interlocked.Exchange`.

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
PoEformance.App --record session.rec           # same, capturing everything
PoEformance.App --replay session.rec           # rerun against the capture, no game needed
```
