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
and a timer bar drawn from the game's own remaining and total duration. The icon sheet is a PNG
of your own choosing (any tile grid; the tab has a picker); without one each effect is drawn as
its coloured disc with its caption on it. The monster half is the one setting that costs a read
per monster, so it is off until switched on and takes its cost with it when switched off.

The config window has a **Map** tab showing the same layout at a readable size, with the
player and nearby markers on it. It stays open while the overlay runs and its settings apply
immediately — no restart. Auto flask is off until switched on there, per belt slot; the key each flask
uses is **read from the game's own config**, never assumed, and shown read-only beside the
slot it belongs to.

## License

MIT.
