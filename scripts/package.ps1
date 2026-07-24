<#
.SYNOPSIS
  Builds self-contained SteamDisc bundles - the launcher, the authoring GUI, and the disc
  installer - for one or more runtimes, and optionally opens a draft GitHub release.

.DESCRIPTION
  For each runtime identifier it publishes three apps self-contained (no .NET needed on the
  target) into a staging folder:

    SteamDisc(.exe)          the launcher / hub - the entry point
    SteamDisc.Author(.exe)   the authoring GUI
    Setup(.exe)              the skinned disc installer (also stamped onto each disc)

  The Author app finds the installer beside it, so the bundle works from one folder.

  Windows bundles are zipped. Other platforms are tar.gz'd so the executable bit survives -
  which means non-Windows bundles should be built on their own OS (the GitHub workflow does
  exactly that with a matrix).

.PARAMETER Version
  Release version, e.g. 0.1.0. A leading "v" is stripped. Defaults to the <Version> in
  Directory.Build.props.

.PARAMETER Runtimes
  One or more runtime identifiers, e.g. win-x64, linux-x64, osx-arm64. Default: win-x64.

.PARAMETER CreateRelease
  Also create a GitHub release with every archive attached (needs the gh CLI, authenticated).
  The release is a DRAFT unless -Publish is given.

.PARAMETER Publish
  Publish the release immediately instead of leaving it as a draft.

.EXAMPLE
  ./scripts/package.ps1
  ./scripts/package.ps1 -Runtimes win-x64,linux-x64,osx-arm64
  ./scripts/package.ps1 -Version 0.2.0 -CreateRelease            # opens a draft release
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string[]]$Runtimes = @('win-x64'),
    [string]$Configuration = 'Release',
    [switch]$CreateRelease,
    [switch]$Publish,
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$hostIsWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)

function Resolve-Dotnet {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return 'dotnet' }
    } catch { }
    $userDotnet = Join-Path $HOME '.dotnet/dotnet.exe'
    if (Test-Path $userDotnet) { return $userDotnet }
    $userDotnetNix = Join-Path $HOME '.dotnet/dotnet'
    if (Test-Path $userDotnetNix) { return $userDotnetNix }
    throw "No .NET SDK found. Install the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)."
}

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
$Version = $Version -replace '^v', ''
if (-not $Tag) { $Tag = "v$Version" }

$dotnet = Resolve-Dotnet
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Packaging SteamDisc $Version for: $($Runtimes -join ', ')" -ForegroundColor Cyan

# project path -> published base name
$apps = @(
    @{ Project = 'src/SteamDisc.Launcher';    Name = 'SteamDisc' },
    @{ Project = 'src/SteamDisc.Builder.App'; Name = 'SteamDisc.Author' },
    @{ Project = 'src/SteamDisc.Runtime.App'; Name = 'Setup' }
)

$artifacts = @()

foreach ($rid in $Runtimes) {
    $ridIsWindows = $rid -like 'win-*'
    $ext = if ($ridIsWindows) { '.exe' } else { '' }

    $stageName = "SteamDisc-$Version-$rid"
    $stage = Join-Path $dist $stageName
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    Write-Host "== $rid" -ForegroundColor Cyan

    $publishArgs = @(
        '-c', $Configuration, '-r', $rid, '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '--nologo', '-v', 'minimal'
    )

    foreach ($app in $apps) {
        $out = Join-Path $dist ("_publish/$rid/" + (Split-Path $app.Project -Leaf))
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }

        $exeName = $app.Name + $ext
        Write-Host "  publishing $($app.Project) -> $exeName" -ForegroundColor DarkCyan
        & $dotnet publish $app.Project @publishArgs -o $out
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $($app.Project) ($rid)" }

        $built = Join-Path $out $exeName
        if (-not (Test-Path $built)) { throw "expected $exeName in $out" }
        Copy-Item $built (Join-Path $stage $exeName) -Force
    }

    # A short read-me so the bundle explains itself.
    $readme = @(
        "SteamDisc $Version ($rid)",
        "",
        "  SteamDisc$ext          Start here. Choose to create a disc or install one.",
        "  SteamDisc.Author$ext   The authoring tool (also opened by the launcher).",
        "  Setup$ext              The disc installer; the authoring tool stamps a copy onto each disc.",
        "",
        "A personal-archival tool for games you already own. It does not acquire games and does not",
        "bypass anything: Steam still owns licensing, updates, and launch."
    )
    if (-not $ridIsWindows) {
        $readme += @(
            "",
            "Note: burning is Windows-only (it hands off to the built-in Disc Image Burner).",
            "On this platform SteamDisc builds the ISO; burn it with your own tool."
        )
    }
    Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding utf8

    foreach ($doc in @('LICENSE', 'LICENSE.txt', 'LICENSE.md')) {
        $path = Join-Path $repoRoot $doc
        if (Test-Path $path) { Copy-Item $path $stage -Force }
    }

    if ($ridIsWindows) {
        $archive = Join-Path $dist "$stageName.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Compress-Archive -Path $stage -DestinationPath $archive
    }
    else {
        # tar keeps the executable bit; zip on Windows would not.
        if (-not $hostIsWindows) {
            foreach ($app in $apps) { & chmod +x (Join-Path $stage $app.Name) }
        }
        $archive = Join-Path $dist "$stageName.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        & tar -czf $archive -C $dist $stageName
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    }

    $sizeMb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "  wrote $archive ($sizeMb MB)" -ForegroundColor Green
    $artifacts += $archive
}

Remove-Item (Join-Path $dist '_publish') -Recurse -Force -ErrorAction SilentlyContinue

if ($CreateRelease) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install GitHub CLI or run without -CreateRelease."
    }

    $ghArgs = @('release', 'create', $Tag) + $artifacts + @(
        '--title', "SteamDisc $Version",
        '--notes', "SteamDisc $Version - self-contained builds. Run SteamDisc to start."
    )
    if (-not $Publish) { $ghArgs += '--draft' }

    $state = if ($Publish) { 'published' } else { 'draft' }
    Write-Host "Creating $state GitHub release $Tag" -ForegroundColor Cyan
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

Write-Host "Done. $($artifacts.Count) archive(s) in dist/." -ForegroundColor Green
