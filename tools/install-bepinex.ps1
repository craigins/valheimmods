<#
.SYNOPSIS
  Installs or updates BepInExPack_Valheim into a Valheim game folder.

.EXAMPLE
  ./tools/install-bepinex.ps1 -ValheimPath "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ValheimPath,

    [string]$Version = "5.4.2350"
)

if (-not (Test-Path (Join-Path $ValheimPath "valheim.exe"))) {
    throw "No valheim.exe found under '$ValheimPath' - check the path."
}

$url = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/$Version/"
$tmpZip = Join-Path $env:TEMP "BepInExPack_Valheim_$Version.zip"
$tmpDir = Join-Path $env:TEMP "BepInExPack_Valheim_$Version"

Write-Host "Downloading BepInExPack_Valheim $Version..."
Invoke-WebRequest -Uri $url -OutFile $tmpZip

if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

$src = Join-Path $tmpDir "BepInExPack_Valheim"

Write-Host "Installing into $ValheimPath ..."
Copy-Item -Path (Join-Path $src "BepInEx") -Destination $ValheimPath -Recurse -Force
Copy-Item -Path (Join-Path $src "doorstop_config.ini") -Destination $ValheimPath -Force
Copy-Item -Path (Join-Path $src "winhttp.dll") -Destination $ValheimPath -Force
Copy-Item -Path (Join-Path $src ".doorstop_version") -Destination $ValheimPath -Force

Write-Host "Done. BepInEx $Version installed at $ValheimPath"
Write-Host "Launch valheim.exe directly (not through a separate loader) - winhttp.dll bootstraps BepInEx automatically."
