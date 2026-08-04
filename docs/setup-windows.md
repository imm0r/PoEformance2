# Running PoEformance on Windows

The short version: **clone once, then never again.** After the one-time setup, staying
current is `git pull` (the run script even does that for you), builds are incremental
(seconds), and offset changes need **no build at all** — the schema is a JSON file the
running app hot-reloads.

## One-time setup (~5 minutes)

1. **Install the .NET 10 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0
   (SDK, x64 installer). Verify in a terminal:

   ```powershell
   dotnet --version     # should print 10.x
   ```

2. **Clone the repo** (once, wherever you like):

   ```powershell
   git clone https://github.com/imm0r/PoEformance2.git
   cd PoEformance2
   ```

3. **Allow local scripts** (once per machine, current user only):

   ```powershell
   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
   ```

That's it. No Visual Studio required — any editor works (VS Code with the C# Dev Kit
extension is the comfortable choice, but even Notepad on the schema file is fine).

## The daily loop

Open **PowerShell as Administrator** (reading another process's memory requires it —
this is the same reason the AHK tool needed elevation), `cd` into the repo, then:

```powershell
.\scripts\run.ps1
```

That single command: pulls the latest changes → builds incrementally (first run ~30 s,
after that a few seconds, and zero if nothing changed) → attaches to the running game →
prints the drift report → exits.

### Why you never re-clone

A git clone downloads the whole history; `git pull` downloads only what changed since
your last pull. They end in the identical state. The run script executes
`git pull --ff-only` for you on every start, so "get the newest version and run it" is
literally the one command above. (`--ff-only` means: if you ever edited files locally
and the pull would conflict, it stops and tells you instead of merging surprises.)

### The modes

```powershell
.\scripts\run.ps1 -Watch          # the RE loop - see below
.\scripts\run.ps1 -Verbose        # show passing/skipped rows too, not just failures
.\scripts\run.ps1 -Record s.rec   # capture the whole memory session to a file
.\scripts\run.ps1 -Replay s.rec   # re-run against a capture, game closed
.\scripts\run.ps1 -NoPull         # skip the git pull (offline / mid-experiment)
.\scripts\test.ps1                # run the test suite (no game needed)
```

## `-Watch`: offsets change with zero builds

This is the mode the whole architecture was built for:

```powershell
.\scripts\run.ps1 -Watch
```

The app attaches **once**, prints the report, and then watches
`schema\poe2.offsets.json`. Every time you save that file it re-runs the report against
the still-attached game — typically well under a second, because the module scan is
cached.

So when the next "new offsets" post appears in the Discord channel:

1. open `schema\poe2.offsets.json` in any editor,
2. change the numbers, Ctrl+S,
3. read the fresh report in the still-running window.

No compile, no restart, no re-attach, no re-clone. A malformed edit (typo, missing
comma) prints a schema error naming the struct/field and the watcher keeps running —
fix and save again.

## No SDK at all: the auto-published build

Every push to `main` compiles on GitHub's servers and updates a rolling release, so
there is one stable download link that always carries the newest build:

**https://github.com/imm0r/PoEformance2/releases/tag/latest-dev**

Download `PoEformance-win-x64.zip`, unzip anywhere, run `PoEformance.App.exe` from an
elevated terminal — no .NET install, no git, no build. `schema\poe2.offsets.json` sits
next to the exe and stays hot-editable there too (`PoEformance.App.exe --watch` works
exactly like from source). Per-commit builds of older states are additionally kept as
Actions artifacts.

This path is for "just run it" days; the git + SDK path is for development, because it
gives you `-Replay`, the tests, and instant code updates.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `could not open it for reading` | Terminal is not elevated. Right-click PowerShell → *Run as administrator*. |
| `PathOfExile process not found` | Game not running, or a process name we don't know yet — check Task Manager for the exact name and report it. |
| `MISS` on statics | The game patched and a byte pattern broke — this is real information, not a tool error. Report which ones. |
| `FAIL` rows in the drift report | An offset drifted. That is the report doing its job — the row names the struct, field and observed value. |
| Script refuses to run | Execution policy — run the `Set-ExecutionPolicy` line from the setup once. |
| `git pull` refuses (`--ff-only`) | You have local edits that conflict. `git stash` them, pull, `git stash pop` — or ask and we sort it out. |
