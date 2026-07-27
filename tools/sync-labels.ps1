# Pull the newest HF Studio label export into the mod, verify it, and build.
#
# Labels are read once at startup now, so painting in the browser no longer shows up live: the
# loop is export-all in HF Studio -> run this -> relaunch. Verification is the point of the
# script, not the copy: a truncated or empty export used to deploy silently and the water simply
# went back to colour guessing with nothing in the log to say why.
#
#   .\tools\sync-labels.ps1              # newest radiance-labels*.json from Documents\HF-Studio
#   .\tools\sync-labels.ps1 -NoBuild     # copy + verify only
#   .\tools\sync-labels.ps1 -Source path\to\export.json

[CmdletBinding()]
param(
    [string] $Source,
    [switch] $NoBuild,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'labels\water-labels.json'

if (-not $Source) {
    $studio = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'HF-Studio'
    $newest = Get-ChildItem -Path $studio -Filter 'radiance-labels*.json' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest) { throw "No radiance-labels*.json in $studio. Export all sheets from HF Studio first." }
    $Source = $newest.FullName
}
if (-not (Test-Path $Source)) { throw "Not found: $Source" }

Write-Host "source  $Source" -ForegroundColor Cyan
Write-Host ("        {0:n2} MB, written {1}" -f ((Get-Item $Source).Length / 1MB), (Get-Item $Source).LastWriteTime)

# ---- verify BEFORE overwriting: a bad export must not replace a good one -------------------
$json = Get-Content $Source -Raw | ConvertFrom-Json
if (-not $json.sheets) { throw "No 'sheets' object: this is not an export-all file." }

$sheetNames = $json.sheets.PSObject.Properties.Name
$tiles = 0
$hist = @{}
foreach ($n in $sheetNames) {
    $t = $json.sheets.$n.tiles
    if (-not $t) { continue }
    foreach ($p in $t.PSObject.Properties) {
        $tiles++
        foreach ($b in [Convert]::FromBase64String($p.Value)) {
            if ($b -ne 0) { $hist[$b] = 1 + $(if ($hist.ContainsKey($b)) { $hist[$b] } else { 0 }) }
        }
    }
}
if ($tiles -eq 0) { throw "Export contains 0 painted tiles: refusing to overwrite $dest." }

$names = @{ 1 = 'water'; 9 = 'ice'; 10 = 'falling'; 11 = 'lava'; 12 = 'window'; 4 = 'deck' }
Write-Host ("sheets  {0}" -f $sheetNames.Count) -ForegroundColor Green
Write-Host ("tiles   {0}" -f $tiles) -ForegroundColor Green
foreach ($k in ($hist.Keys | Sort-Object)) {
    $label = if ($names.ContainsKey([int]$k)) { $names[[int]$k] } else { "class $k" }
    Write-Host ("        {0,-8} {1,12:n0} px" -f $label, $hist[$k])
}

New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
Copy-Item $Source $dest -Force
Write-Host "copied  -> $dest" -ForegroundColor Green

if ($NoBuild) { return }

# The DLL is locked while the game runs, so a build would fail halfway through deploying.
$running = Get-Process -Name 'Stardew Valley', 'StardewModdingAPI' -ErrorAction SilentlyContinue
if ($running) { throw 'Close Stardew Valley first: its files are locked while it runs.' }

Push-Location $root
try { dotnet build -c $Configuration -v m }
finally { Pop-Location }
