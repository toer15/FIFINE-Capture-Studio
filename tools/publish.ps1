param(
    [ValidateSet("framework", "selfcontained")]
    [string]$Mode = "framework",
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FifineCaptureStudio\FifineCaptureStudio.csproj"
$outDir = Join-Path $root ("artifacts\publish\" + $Mode)

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$args = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $outDir,
    "-p:PublishReadyToRun=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

if ($Mode -eq "selfcontained") {
    $args += @("--self-contained", "true", "-p:PublishSingleFile=false")
} else {
    $args += @("--self-contained", "false")
}

Write-Host "dotnet $($args -join ' ')" -ForegroundColor DarkGray
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published to $outDir" -ForegroundColor Green

if ($Installer) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup 6 was not found. Install it, then run ISCC.exe installer\FifineCaptureStudio.iss"
        exit 0
    }

    & $iscc "/DPublishDir=$outDir" (Join-Path $root "installer\FifineCaptureStudio.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    Write-Host "Installer built into installer\output" -ForegroundColor Green
}
