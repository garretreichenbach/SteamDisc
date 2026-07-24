<#
.SYNOPSIS
  Builds a self-contained SteamDisc release: the launcher, the authoring GUI, and the disc
  installer, published as single-file Windows executables and zipped for a GitHub release.

.DESCRIPTION
  Publishes three apps self-contained (no .NET needed on the target) into a staging folder:

    SteamDisc.exe          the launcher / hub — the entry point
    SteamDisc.Author.exe   the authoring GUI
    Setup.exe              the skinned disc installer (also what gets stamped onto discs)

  The Author app finds Setup.exe beside it, so the whole bundle works from one folder.

.PARAMETER Version
  Release version, e.g. 0.1.0. A leading "v" is stripped. Defaults to the <Version> in
  Directory.Build.props.

.PARAMETER Runtime
  Target runtime identifier. Default win-x64.

.PARAMETER CreateRelease
  Also create a GitHub release with the zip attached (needs the gh CLI, authenticated).

.PARAMETER Tag
  Git tag for the release. Defaults to "v<Version>".

.EXAMPLE
  ./scripts/package.ps1
  ./scripts/package.ps1 -Version 0.2.0 -CreateRelease
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$CreateRelease,
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Resolve-Dotnet {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return 'dotnet' }
    } catch { }
    $user = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $user) { return $user }
    throw "No .NET SDK found. Install the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)."
}

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
$Version = $Version -replace '^v', ''
if (-not $Tag) { $Tag = "v$Version" }

$dotnet = Resolve-Dotnet
Write-Host "Packaging SteamDisc $Version ($Runtime) with $dotnet" -ForegroundColor Cyan

$dist = Join-Path $repoRoot 'dist'
$stageName = "SteamDisc-$Version-$Runtime"
$stage = Join-Path $dist $stageName
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$publishArgs = @(
    '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '--nologo', '-v', 'minimal'
)

# project path, published exe name
$apps = @(
    @{ Project = 'src/SteamDisc.Launcher';    Exe = 'SteamDisc.exe' },
    @{ Project = 'src/SteamDisc.Builder.App'; Exe = 'SteamDisc.Author.exe' },
    @{ Project = 'src/SteamDisc.Runtime.App'; Exe = 'Setup.exe' }
)

foreach ($app in $apps) {
    $out = Join-Path $dist ("_publish/" + (Split-Path $app.Project -Leaf))
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "  publishing $($app.Project) -> $($app.Exe)" -ForegroundColor DarkCyan
    & $dotnet publish $app.Project @publishArgs -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($app.Project)" }
    Copy-Item (Join-Path $out $app.Exe) (Join-Path $stage $app.Exe) -Force
}

# A short read-me so the bundle explains itself (array of lines; no here-strings for PS 5.1).
$readme = @(
    "SteamDisc $Version",
    "",
    "  SteamDisc.exe          Start here. Choose to create a disc or install one.",
    "  SteamDisc.Author.exe   The authoring tool (also opened by the launcher).",
    "  Setup.exe              The disc installer; the authoring tool stamps a copy onto each disc.",
    "",
    "A personal-archival tool for games you already own. It does not acquire games and does not",
    "bypass anything: Steam still owns licensing, updates, and launch."
)
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding utf8

foreach ($doc in @('LICENSE', 'LICENSE.txt', 'LICENSE.md')) {
    $path = Join-Path $repoRoot $doc
    if (Test-Path $path) { Copy-Item $path $stage -Force }
}

$zip = Join-Path $dist "$stageName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item (Join-Path $dist '_publish') -Recurse -Force -ErrorAction SilentlyContinue

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Wrote $zip ($sizeMb MB)" -ForegroundColor Green

if ($CreateRelease) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install GitHub CLI or run without -CreateRelease."
    }
    Write-Host "Creating GitHub release $Tag" -ForegroundColor Cyan
    gh release create $Tag $zip --title "SteamDisc $Version" --notes "SteamDisc $Version - self-contained Windows build. Run SteamDisc.exe to start."
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

Write-Host "Done." -ForegroundColor Green
