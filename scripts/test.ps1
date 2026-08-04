#Requires -Version 5
# Runs the whole test suite (no game, any machine). Use this to check a change before
# handing it over - all 30+ tests run against synthetic memory and recordings.
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try { dotnet test --nologo } finally { Pop-Location }
