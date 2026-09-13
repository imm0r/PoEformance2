#Requires -Version 7
<#
.SYNOPSIS
  Download the minimap icons named by data\minimap-icons.urls.txt from poe2db.

.DESCRIPTION
  WHY THE RECIPE IS COMMITTED AND THE PICTURES ARE NOT. The manifest names 653
  .webp icons - GGG's art, served by a third party - and this script pulls the
  lot in about half a minute. The repository carries the list and the fetcher
  rather than 2.6 MB of somebody else's game art.

  THE PART THAT IS NOT OBVIOUS, and it is measured rather than assumed:
  cdn.poe2db.tw is Cloudflare in front of an Apache origin, and the ORIGIN
  refuses any request whose Referer does not mention poe2db.tw, with a
  279-byte HTML 403 page. A plain substring is all it checks - no header at
  all is refused and so is "https://example.com/", while "poe2db.tw" with no
  scheme and even "https://evil.com/poe2db.tw/" are let through - and the
  User-Agent is never looked at. So this sends the Referer it honestly is, the
  site the icons belong to, and does not dress up as a browser.

  TWO WAYS THAT BITES SOMEBODY FETCHING BY HAND. First, Invoke-WebRequest
  -OutFile without the header writes the 403 page to disk under the .webp name
  and reports nothing wrong; the failure surfaces much later, in whatever tries
  to decode it. Every response here is checked for the RIFF....WEBP magic
  before it is written, so a file that exists is an image - which also makes a
  re-run repair a broken folder rather than trust it.

  Second, the edge cache HIDES the rule. Once a URL has been fetched
  successfully, Cloudflare serves it from cache to header-less requests too
  (cf-cache-status: HIT), so a quick check that "it works without the Referer"
  really only proves that somebody already sent one. Append a random query
  string to reach the origin again:

      curl -sS -o /dev/null -w '%{http_code}' 'https://cdn.poe2db.tw/image/Art/2DArt/minimap/player/MyPlayer.webp?cb=1'

.EXAMPLE
  .\scripts\fetch-minimap-icons.ps1
      Fetch everything the manifest lists into assets\minimap-icons\, which is
      gitignored. Icons already on disk are skipped, so a re-run costs nothing
      and only fills the gaps.

.EXAMPLE
  .\scripts\fetch-minimap-icons.ps1 -OutputDir D:\icons -Parallel 32
      Somewhere else, with a wider window.

.EXAMPLE
  .\scripts\fetch-minimap-icons.ps1 -Force
      Re-fetch every icon, including the ones already there.
#>

[CmdletBinding()]
param(
    # Manifest of image URLs, one per line. Blank lines and # comments are ignored.
    # Defaults to data\minimap-icons.urls.txt.
    [string]$UrlList,

    # Where the .webp files land. Created if missing. Defaults to assets\minimap-icons.
    [string]$OutputDir,

    # Requests in flight at once. Also the connection cap on the handler, since
    # every URL in the manifest is the same host.
    [ValidateRange(1, 64)]
    [int]$Parallel = 16,

    # Extra attempts per URL before it is reported as failed.
    [ValidateRange(0, 10)]
    [int]$Retries = 4,

    # Re-fetch icons that are already on disk and intact.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $UrlList)   { $UrlList   = Join-Path -Path $repoRoot -ChildPath 'data' -AdditionalChildPath 'minimap-icons.urls.txt' }
if (-not $OutputDir) { $OutputDir = Join-Path -Path $repoRoot -ChildPath 'assets' -AdditionalChildPath 'minimap-icons' }

if (-not (Test-Path -LiteralPath $UrlList)) {
    Write-Host "No URL manifest at $UrlList" -ForegroundColor Red
    exit 2
}

# 'RIFF' <4-byte size> 'WEBP'. Telling those twelve bytes apart from the origin's
# 403 page is the entire reason this script exists rather than a one-line
# Invoke-WebRequest loop; both arrive over a perfectly healthy connection.
$script:RiffWebp = [byte[]](0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50)

function Test-WebPMagic {
    param([byte[]]$Bytes, [int]$Length = -1)

    if ($null -eq $Bytes) { return $false }
    if ($Length -lt 0) { $Length = $Bytes.Length }
    if ($Length -lt 12) { return $false }

    for ($i = 0; $i -lt 4; $i++) {
        if ($Bytes[$i] -ne $script:RiffWebp[$i]) { return $false }
        if ($Bytes[$i + 8] -ne $script:RiffWebp[$i + 8]) { return $false }
    }
    return $true
}

function Test-WebPFile {
    param([string]$Path)

    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $head = [byte[]]::new(12)
            return Test-WebPMagic -Bytes $head -Length $stream.Read($head, 0, 12)
        }
        finally { $stream.Dispose() }
    }
    catch { return $false }
}

$null = New-Item -ItemType Directory -Force -Path $OutputDir

$queue = [System.Collections.Generic.Queue[hashtable]]::new()
$skipped = 0
foreach ($line in [System.IO.File]::ReadLines($UrlList)) {
    $url = $line.Trim()
    if ($url.Length -eq 0 -or $url.StartsWith('#')) { continue }

    $name = $url.Substring($url.LastIndexOf('/') + 1)
    $query = $name.IndexOf('?')
    if ($query -ge 0) { $name = $name.Substring(0, $query) }
    if ($name.Length -eq 0) {
        Write-Host "Skipping URL with no file name: $url" -ForegroundColor Yellow
        continue
    }

    $path = Join-Path $OutputDir $name
    if (-not $Force -and (Test-Path -LiteralPath $path) -and (Test-WebPFile $path)) {
        $skipped++
        continue
    }

    $queue.Enqueue(@{ Url = $url; Path = $path; Name = $name; Attempt = 0; Task = $null })
}

$wanted = $queue.Count
if ($wanted -eq 0) {
    Write-Host "Nothing to do - $skipped icons already in $OutputDir" -ForegroundColor DarkGray
    exit 0
}

$handler = [System.Net.Http.SocketsHttpHandler]::new()
$handler.MaxConnectionsPerServer = $Parallel
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::All
$client = [System.Net.Http.HttpClient]::new($handler, $true)
$client.Timeout = [TimeSpan]::FromSeconds(60)
# .NET spells the misspelled HTTP header correctly; this is the Referer header.
$client.DefaultRequestHeaders.Referrer = [Uri]'https://poe2db.tw/'

# A sliding window rather than waves of $Parallel: one slow response should hold
# up its own slot, not the fifteen that finished next to it. One HttpClient for
# all of them keeps the connections open instead of paying a TLS handshake per
# icon, which is most of the cost when the payloads are three kilobytes.
$inflight = [System.Collections.Generic.List[hashtable]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$downloaded = 0
$bytes = 0L
$clock = [System.Diagnostics.Stopwatch]::StartNew()

try {
    while ($queue.Count -gt 0 -or $inflight.Count -gt 0) {
        while ($inflight.Count -lt $Parallel -and $queue.Count -gt 0) {
            $job = $queue.Dequeue()
            $job.Task = $client.GetByteArrayAsync($job.Url)
            $inflight.Add($job)
        }

        $tasks = [System.Threading.Tasks.Task[]]::new($inflight.Count)
        for ($i = 0; $i -lt $inflight.Count; $i++) { $tasks[$i] = $inflight[$i].Task }

        $index = [System.Threading.Tasks.Task]::WaitAny($tasks)
        $job = $inflight[$index]
        $inflight.RemoveAt($index)

        $payload = $null
        $reason = $null
        if ($job.Task.IsCompletedSuccessfully) {
            $payload = $job.Task.Result
            if (-not (Test-WebPMagic -Bytes $payload)) {
                # Nearly always the hotlink 403 described at the top of this file.
                $reason = "not a WebP - $($payload.Length) bytes"
                $payload = $null
            }
        }
        else {
            $reason = $job.Task.Exception.GetBaseException().Message
        }

        if ($null -ne $payload) {
            [System.IO.File]::WriteAllBytes($job.Path, $payload)
            $downloaded++
            $bytes += $payload.Length
        }
        elseif ($job.Attempt -lt $Retries) {
            $job.Attempt++
            $job.Task = $null
            # Back of the queue is spacing enough while there is other work; only
            # a retry with an empty queue and nothing in flight has to wait for
            # itself, so that is the one case that sleeps.
            if ($queue.Count -eq 0 -and $inflight.Count -eq 0) {
                Start-Sleep -Milliseconds (250 * $job.Attempt)
            }
            $queue.Enqueue($job)
        }
        else {
            $failures.Add("$($job.Name) - $reason")
        }

        $settled = $downloaded + $failures.Count
        if (($settled % 10) -eq 0 -or $settled -eq $wanted) {
            Write-Progress -Activity 'Fetching minimap icons' `
                -Status "$settled of $wanted" `
                -PercentComplete ([math]::Min(100, 100 * $settled / $wanted))
        }
    }
}
finally {
    $client.Dispose()
    Write-Progress -Activity 'Fetching minimap icons' -Completed
}

$clock.Stop()
Write-Host ("{0} downloaded, {1} already present, {2} failed - {3:n2} MB in {4:n1}s" -f `
    $downloaded, $skipped, $failures.Count, ($bytes / 1MB), $clock.Elapsed.TotalSeconds)
Write-Host "Output: $OutputDir" -ForegroundColor DarkGray

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($failure in $failures) { Write-Host "FAIL $failure" -ForegroundColor Red }
    exit 1
}
