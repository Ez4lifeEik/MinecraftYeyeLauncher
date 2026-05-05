#Requires -Version 5.1
param([switch]$SkipInstaller)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$PublishDir  = Join-Path $ProjectRoot "publish"
$IssFile     = Join-Path $ProjectRoot "installer.iss"

$IsccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "== $msg" -ForegroundColor Cyan
}

Write-Step "1/3  dotnet clean"
dotnet clean "$ProjectRoot" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed" }

Write-Step "2/3  dotnet publish"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item $PublishDir -ItemType Directory | Out-Null

dotnet publish "$ProjectRoot" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    --nologo `
    -o "$PublishDir"

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Step "3/3  Inno Setup"
if ($SkipInstaller) {
    Write-Host "  Skipped (-SkipInstaller)" -ForegroundColor Yellow
} else {
    $iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning "ISCC.exe not found, skipping installer"
    } else {
        Write-Host "  Using: $iscc"
        & $iscc $IssFile
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }
        $installer = Get-ChildItem $PublishDir -Filter "*Setup*.exe" | Select-Object -First 1
        if ($installer) {
            Write-Host ""
            Write-Host "  Installer: $($installer.FullName)" -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "Done -> $PublishDir" -ForegroundColor Green
