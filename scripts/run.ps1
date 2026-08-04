#Requires -Version 5
<#
.SYNOPSIS
  One-command dev loop for PoEformance: update, build, attach to the live game,
  print the drift report.

.DESCRIPTION
  Run this from an ELEVATED PowerShell (Administrator) - reading another process's
  memory needs it. `dotnet run` does an incremental build first, so the only time
  it is slow is the very first run.

.EXAMPLE
  .\scripts\run.ps1
      Pull latest, build, attach, print the report, exit.

.EXAMPLE
  .\scripts\run.ps1 -Watch
      Stay attached and re-run the report every time schema\poe2.offsets.json is
      saved. Edit an offset, Ctrl+S, see the new report - no rebuild, no re-attach.

.EXAMPLE
  .\scripts\run.ps1 -Record session.rec
      Capture the whole memory session to a file for offline replay.

.EXAMPLE
  .\scripts\run.ps1 -Replay session.rec
      Re-run against a recording with the game closed (implies -NoPull).
#>
param(
    [switch]$Watch,
    [string]$Record,
    [string]$Replay,
    [switch]$Verbose,
    [switch]$NoPull
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $NoPull -and -not $Replay) {
        Write-Host 'git pull --ff-only' -ForegroundColor DarkGray
        git pull --ff-only
    }

    $appArgs = @()
    if ($Watch)   { $appArgs += '--watch' }
    if ($Verbose) { $appArgs += '--verbose' }
    if ($Record)  { $appArgs += '--record'; $appArgs += $Record }
    if ($Replay)  { $appArgs += '--replay'; $appArgs += $Replay }

    dotnet run --project src/PoEformance.App -c Release -- @appArgs
}
finally {
    Pop-Location
}
