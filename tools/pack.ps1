param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDir = "Releases",
    [switch]$SelfContained = $true,
    [string]$Channel = "win"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' must be semantic, e.g. 1.0.1 or 1.1.0-beta.1"
}

Write-Host "Publishing $Version (win-x64, self-contained=$SelfContained)"
$publishArgs = @(
    "publish", "src/FifineCaptureStudio/FifineCaptureStudio.csproj",
    "-c", "Release", "-r", "win-x64",
    "-p:Version=$Version",
    "-o", "publish"
)
if ($SelfContained) { $publishArgs += "--self-contained"; $publishArgs += "true" }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing the Velopack CLI (dotnet tool)…"
    dotnet tool install --global vpk
}

$notes = Join-Path $root "release-notes.md"
if (-not (Test-Path $notes)) {
    "## FIFINE Capture Studio $Version" | Out-File -Encoding utf8 $notes
}

vpk pack `
    --packId FifineCaptureStudio `
    --packVersion $Version `
    --packDir publish `
    --mainExe FifineCaptureStudio.exe `
    --packTitle "FIFINE Capture Studio" `
    --packAuthors "FIFINE" `
    --icon "src/FifineCaptureStudio/Assets/fifine.ico" `
    --releaseNotes $notes `
    --channel $Channel `
    --outputDir $OutputDir

if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
Get-ChildItem $OutputDir | Select-Object Name, Length
Write-Host "Local packages are in $OutputDir. Run tools/verify-update.ps1 to test the update path locally."
