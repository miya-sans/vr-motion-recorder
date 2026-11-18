<#
.SYNOPSIS
  Copies the licence and notice files that the app's NuGet packages ship into one folder,
  so a download of the app carries them (see THIRD_PARTY_NOTICES.md).
.EXAMPLE
  ./collect-licenses.ps1 -AssetsFile artifacts/obj/Vrmr.App/project.assets.json -OutDir dist/VRMotionRecorder/licenses
#>
param(
    [Parameter(Mandatory)] [string] $AssetsFile,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'

$assets = Get-Content -Raw -LiteralPath $AssetsFile | ConvertFrom-Json
$roots = @($assets.packageFolders.PSObject.Properties.Name)
$names = 'LICENSE*', 'License*', 'COPYING*', 'NOTICE*', 'THIRD-PARTY-NOTICES*', 'ThirdPartyNotices*'
$copied = 0

foreach ($lib in $assets.libraries.PSObject.Properties) {
    if ($lib.Value.type -ne 'package') { continue }
    $dir = $roots | ForEach-Object { Join-Path $_ $lib.Value.path } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $dir) { continue }
    $files = Get-ChildItem -LiteralPath $dir -File | Where-Object { $f = $_; $names | Where-Object { $f.Name -like $_ } }
    if (-not $files) { continue }
    $id = ($lib.Name -split '/')[0]
    $dest = Join-Path $OutDir $id
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $files | Copy-Item -Destination $dest -Force
    $copied += @($files).Count
}
Write-Host "Copied $copied licence and notice files into $OutDir"
