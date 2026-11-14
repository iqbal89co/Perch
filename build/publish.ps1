# Builds the Perch release: runs the tests, publishes a self-contained single-file Perch.exe and
# compiles the Inno Setup installer into dist/Perch-Setup-<version>.exe.
#   powershell -ExecutionPolicy Bypass -File build/publish.ps1 [-SkipTests]
param([switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
[xml]$proj = Get-Content (Join-Path $root "src\Perch\Perch.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Perch $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "== tests" -ForegroundColor Cyan
    & $dotnet test (Join-Path $root "tests\Perch.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

Write-Host "== publish" -ForegroundColor Cyan
$out = Join-Path $root "dist\app"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $dotnet publish (Join-Path $root "src\Perch\Perch.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $out | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Out-String | Write-Host

Write-Host "== installer" -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$out" (Join-Path $root "installer\Perch.iss")
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
$setup = Join-Path $root "dist\Perch-Setup-$version.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  Perch-Setup-$version.exe" | Set-Content (Join-Path $root "dist\Perch-Setup-$version.exe.sha256") -Encoding ascii
Write-Host ("Installer: {0} ({1} MB)  sha256 {2}" -f $setup, [math]::Round((Get-Item $setup).Length / 1MB, 1), $hash) -ForegroundColor Green
