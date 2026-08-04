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

```bash
dotnet build        # any OS
dotnet test         # any OS

# Windows, game running, elevated shell:
PoEformance.App                          # attach + drift report
PoEformance.App --record session.rec     # capture the session
PoEformance.App --replay session.rec     # develop against it, game closed
```

## License

MIT.
