# Builds HangAll into dist\.
#   scripts\build.ps1            framework-dependent single file (needs the .NET 8 Desktop Runtime), ~25 MB
#   scripts\build.ps1 -Portable  self-contained single file, runs anywhere, ~70 MB
param([switch]$Portable, [switch]$Installer, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\HangAll\HangAll.csproj'
$dist = Join-Path $root 'dist'

$dotnet = @(
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $dotnet) { Write-Error "The .NET SDK was not found"; exit 1 }

# Brand assets are generated from the SVG source. Rebuild them on every package
# build so the executable, tray icon and installer cannot keep a stale ICO.
& (Join-Path $PSScriptRoot 'make-brand.ps1')

$outDir = if ($Portable) { Join-Path $dist 'portable' } else { Join-Path $dist 'win-x64' }

# WPF keeps generated resource indexes under obj. An incremental publish can miss a
# newly added image even though the project file changed, so always clear Release
# intermediates before publishing the signed-off executable and installer.
& $dotnet restore $proj -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet clean $proj -c Release -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$args = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '-o', $outDir,
          '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none')
$args += if ($Portable) { '--self-contained', 'true', '-p:EnableCompressionInSingleFile=true' } else { '--self-contained', 'false' }
# The version from the release tag goes into the exe's own properties too.
if ($Version -and $Version -ne '0.0.0') { $args += "-p:Version=$Version" }
& $dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nBuilt $(Join-Path $outDir 'HangAll.exe')"

# scripts\build.ps1 -Installer  also builds dist\HangAll-<version>-Setup.exe with Inno Setup
if ($Installer) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { Write-Error "Inno Setup 6 or 7 not found"; exit 1 }
    $sourceDir = Join-Path $dist "win-x64"
    if ($Portable) { $sourceDir = Join-Path $dist "portable" }
    if (-not (Test-Path (Join-Path $sourceDir "HangAll.exe"))) { Write-Error "Build HangAll.exe first"; exit 1 }
    $ver = if ($Version) { $Version } else { ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Select-Object -First 1 }
    $script = Join-Path $root 'installer\hangall.iss'
    Write-Host "Inno Setup: $iscc"
    Write-Host "Compiling $script with /DVersion=$ver /DSource=$sourceDir"
    & $iscc $script "/DVersion=$ver" "/DSource=$sourceDir" /Q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "::error::ISCC exited with $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    $setup = Join-Path $dist "HangAll-$ver-Setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) {
        # ISCC can succeed while writing somewhere else; say where the output went.
        $found = @(Get-ChildItem -Recurse -File $dist -Filter '*Setup*.exe' -ErrorAction SilentlyContinue)
        $listing = if ($found.Count -gt 0) { ($found | ForEach-Object { $_.FullName }) -join ', ' } else { 'nothing' }
        Write-Host "::error::Expected $setup but the compiler produced: $listing"
        throw "Inno Setup did not write $setup"
    }
    Write-Host "Built $setup"
}
