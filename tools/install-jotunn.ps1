<#
.SYNOPSIS
  Installs or updates the Jotunn library into a Valheim game folder.

.DESCRIPTION
  Jotunn is a RUNTIME dependency, not just a compile-time one. The JotunnLib NuGet
  package referenced by the csproj only provides reference assemblies to build against -
  it does not put Jotunn.dll into the game. Without this step BepInEx refuses to load
  the mod with:

      Could not load [Craigins Valheim Mod x.y.z] because it has missing dependencies:
      com.jotunn.jotunn

  Keep -Version in step with the JotunnLib <PackageReference> in both csproj files.

.EXAMPLE
  ./tools/install-jotunn.ps1 -ValheimPath "E:\Programs\Steam\steamapps\common\Valheim"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ValheimPath,

    [string]$Version = "2.30.0"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $ValheimPath "valheim.exe"))) {
    throw "No valheim.exe found under '$ValheimPath' - check the path."
}

$pluginsDir = Join-Path $ValheimPath "BepInEx\plugins"
if (-not (Test-Path $pluginsDir)) {
    throw "No BepInEx\plugins under '$ValheimPath' - run tools/install-bepinex.ps1 first."
}

$url = "https://thunderstore.io/package/download/ValheimModding/Jotunn/$Version/"
$tmpZip = Join-Path $env:TEMP "Jotunn_$Version.zip"
$tmpDir = Join-Path $env:TEMP "Jotunn_$Version"

Write-Host "Downloading Jotunn $Version..."
Invoke-WebRequest -Uri $url -OutFile $tmpZip

if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

$dest = Join-Path $pluginsDir "Jotunn"
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }

Copy-Item -Path (Join-Path $tmpDir "plugins\*") -Destination $dest -Recurse -Force

$installed = (Get-Item (Join-Path $dest "Jotunn.dll")).VersionInfo.FileVersion
Write-Host "Done. Jotunn $installed installed at $dest"
