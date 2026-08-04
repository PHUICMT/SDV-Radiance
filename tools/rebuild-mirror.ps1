# Rebuild the public mirror with incremental commit history
# One commit per release tag, each with a clean archived tree
param([switch]$Push)

$ErrorActionPreference = "Stop"
$devDir = (Get-Item "$PSScriptRoot/..").FullName
$workDir = "$env:TEMP/pub-rebuild"
$zipFile = "$env:TEMP/_archive.zip"
$pubUrl = "https://github.com/PHUICMT/SDV-Radiance.git"
$tags = @('v1.0.0','v1.1.0','v1.2.0','v1.2.1','v1.2.2','v1.3.0','v1.3.1')

# Clean
if (Test-Path $workDir) { Remove-Item -Recurse -Force $workDir }
if (Test-Path $zipFile) { Remove-Item -Force $zipFile }
New-Item -ItemType Directory -Force $workDir | Out-Null

# Init
Push-Location $workDir
git init -b main
git config user.name "PHUICMT"
git config user.email "PHUICMT@users.noreply.github.com"
Pop-Location

foreach ($tag in $tags) {
    Write-Host "=== $tag ===" -ForegroundColor Cyan

    # Checkout tag in dev
    Push-Location $devDir
    git checkout $tag --quiet
    Pop-Location

    # Clear work
    Get-ChildItem -Path $workDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    # Archive clean tree
    Push-Location $devDir
    git archive --format=zip --output=$zipFile HEAD
    Pop-Location
    Expand-Archive -Path $zipFile -DestinationPath $workDir -Force
    Remove-Item $zipFile -Force

    # Strip private
    Remove-Item -Recurse -Force "$workDir/.github" -ErrorAction SilentlyContinue
    Get-ChildItem "$workDir/docs" -Filter "*.md" -File -ErrorAction SilentlyContinue | 
      Where-Object { $_.Name -ne "compatibility.md" -and -not $_.FullName.Contains("docs\public") } | 
      Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$workDir" -Filter "*.local.md" -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Commit
    Push-Location $workDir
    git add -A 2>$null
    $count = (git status --porcelain 2>$null | Measure-Object).Count
    if ($count -gt 0) {
        git commit -m "Release $tag"
        Write-Host "  committed $tag" -ForegroundColor Green
    } else {
        Write-Host "  skip (no changes)" -ForegroundColor Yellow
    }
    Pop-Location
}

# Restore dev to main
Push-Location $devDir
git checkout main --quiet
Pop-Location

Write-Host "`n=== History ===" -ForegroundColor Green
Push-Location $workDir
git --no-pager log --oneline
Pop-Location

if ($Push) {
    $token = $env:MIRROR_TOKEN
    if (-not $token) {
        Write-Host "ERROR: MIRROR_TOKEN env var not set" -ForegroundColor Red
        exit 1
    }
    Push-Location $workDir
    $remote = "https://x-access-token:${token}@github.com/PHUICMT/SDV-Radiance.git"
    git remote add mirror $remote 2>$null
    git push mirror main --force
    git push mirror --tags --force
    Pop-Location
    Write-Host "=== Pushed to public mirror ===" -ForegroundColor Green
} else {
    Write-Host "To push: cd $workDir && git remote add mirror $pubUrl && git push mirror main --tags --force"
}