# Builds the Rimlink client mod and assembles a mod folder.
#   .\scripts\Build-ClientMod.ps1 [-RimWorldDir <game folder>] [-OutputDir <folder>] [-InstallTo <Mods folder>] [-BundleHarmony]
# -BundleHarmony copies 0Harmony.dll from the NuGet cache next to the mod assembly for local test runs without the
# Harmony mod. Release builds must NOT bundle it: players install the Harmony mod (brrainz.harmony).

param(
    [string]$RimWorldDir = 'D:\SteamLibrary\steamapps\common\RimWorld',
    [string]$OutputDir = '',
    [string]$InstallTo = '',
    [switch]$BundleHarmony
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'build\mod\Rimlink' }
if (-not (Test-Path (Join-Path $RimWorldDir 'Assembly-CSharp.dll'))) {
    throw "RimWorld not found at '$RimWorldDir'. Pass -RimWorldDir."
}

$project = Join-Path $root 'src\Rimlink.Client\Rimlink.Client.csproj'
$binDir = Join-Path $root 'src\Rimlink.Client\bin\Release\net10.0'
dotnet build $project -c Release -nologo -v q "-p:RimWorldDir=$RimWorldDir"
if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }

if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Force $OutputDir | Out-Null
Copy-Item (Join-Path $root 'mod\*') $OutputDir -Recurse

# Only the DLLs the spike proved necessary; the game already ships Microsoft.Extensions.* and DiagnosticSource.
$assemblies = Join-Path $OutputDir 'Assemblies'
New-Item -ItemType Directory -Force $assemblies | Out-Null
$ship = @('Rimlink.Client.dll', 'Rimlink.Client.Core.dll', 'Rimlink.Protocol.dll',
          'Grpc.Net.Client.dll', 'Grpc.Net.Common.dll', 'Grpc.Core.Api.dll', 'Google.Protobuf.dll', 'ZstdSharp.dll')
foreach ($name in $ship) {
    $src = Join-Path $binDir $name
    if (-not (Test-Path $src)) { throw "missing $name in $binDir" }
    Copy-Item $src $assemblies
}
if ($BundleHarmony) {
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $harmony = Get-ChildItem (Join-Path $nuget 'lib.harmony') -Recurse -Filter '0Harmony.dll' |
        Where-Object { $_.FullName -match '\\net10\.0\\' } | Select-Object -First 1
    if (-not $harmony) { throw '0Harmony.dll (net10.0) not found in the NuGet cache' }
    Copy-Item $harmony.FullName $assemblies
    Write-Host 'Bundled 0Harmony.dll (dev build only)'
}

Write-Host "Mod assembled at $OutputDir"
Get-ChildItem $assemblies | ForEach-Object { Write-Host "  Assemblies\$($_.Name)" }

if ($InstallTo) {
    $target = Join-Path $InstallTo 'Rimlink'
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item $OutputDir $target -Recurse
    Write-Host "Installed to $target"
}

