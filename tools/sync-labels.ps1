# Fold the newest HF Studio label export into the mod's label DB, then build.
#
# Labels are read once at startup, so painting in the browser does not show up live: the loop is
# paint -> run this -> relaunch.
#
# This USED to copy the export over labels\water-labels.json. That is only correct while the
# export is a superset of what ships, and on 2026-08-14 it stopped being one: the export had
# 2,095 new liquid tiles but had also lost a whole sheet's glass (renamed with a leading dot and
# emptied) and 9 winter veto tiles, and a copy would have taken the loss silently. The old
# verification could not have caught either one - it counted painted pixels, and veto tiles have
# none. So the merge, and the decision about what wins, now lives in one place that can explain
# itself:
#
#   tools\labelops\mergelabels.py   - export wins conflicts, DB fills gaps, nothing is dropped
#
#   .\tools\sync-labels.ps1              # newest radiance-labels*.json from Documents\HF-Studio
#   .\tools\sync-labels.ps1 -NoBuild     # merge only
#   .\tools\sync-labels.ps1 -DryRun      # report what the merge would do, write nothing
#   .\tools\sync-labels.ps1 -Source path\to\export.json

[CmdletBinding()]
param(
    [string] $Source,
    [switch] $NoBuild,
    [switch] $DryRun,
    [string] $Python,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$merge = Join-Path $PSScriptRoot 'labelops\mergelabels.py'

if (-not $Python) {
    foreach ($candidate in @('py', 'python', 'python3')) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) { $Python = $cmd.Source; break }
    }
}
if (-not $Python) { throw 'No python on PATH. Pass -Python <path to python.exe>.' }

$mergeArgs = @($merge)
if ($Source) {
    if (-not (Test-Path $Source)) { throw "Not found: $Source" }
    $mergeArgs += @('--export', $Source)
}
if ($DryRun) { $mergeArgs += '--dry-run' }

# The merge refuses to write when a single tile the mod already ships would go missing, so a
# non-zero exit here means the DB was left exactly as it was.
& $Python @mergeArgs
if ($LASTEXITCODE -ne 0) { throw "mergelabels.py failed ($LASTEXITCODE): labels\water-labels.json untouched." }

if ($DryRun -or $NoBuild) { return }

# The DLL is locked while the game runs, so a build would fail halfway through deploying.
$running = Get-Process -Name 'Stardew Valley', 'StardewModdingAPI' -ErrorAction SilentlyContinue
if ($running) { throw 'Close Stardew Valley first: its files are locked while it runs.' }

Push-Location $root
try { dotnet build -c $Configuration -v m }
finally { Pop-Location }
