# Builds the dev-only BridgeTools companion (rimlink/* tools for RimBridgeServer) and deploys it for GABS runs.
#   .\scripts\Build-BridgeTools.ps1 [-RimWorldDir <game folder>] [-InstallTo <BridgeTools root>] [-NoInstall]
# Deploys exactly one file, <InstallTo>\Rimlink\Rimlink.BridgeTools.dll (default root <RimWorldDir>\BridgeTools).
# RimBridgeServer probes the companion folder first for dependencies: a copy of Rimlink.Client.dll there would load a
# second Rimlink.Client with its own, always empty session. The script refuses a folder holding anything else.
# Never part of a release: Package-Release.ps1 does not call this script. Close the game first (it locks the DLL);
# companions load at bridge start, so restart the game after deploying.

param(
    [string]$RimWorldDir = 'D:\SteamLibrary\steamapps\common\RimWorld',
    [string]$InstallTo = '',
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RimWorldDir 'Assembly-CSharp.dll'))) {
    throw "RimWorld not found at '$RimWorldDir'. Pass -RimWorldDir."
}
if (-not $InstallTo) { $InstallTo = Join-Path $RimWorldDir 'BridgeTools' }

$project = Join-Path $root 'tools\Rimlink.BridgeTools\Rimlink.BridgeTools.csproj'
$binDir = Join-Path $root 'tools\Rimlink.BridgeTools\bin\Release\net10.0'
dotnet build $project -c Release -nologo -v q "-p:RimWorldDir=$RimWorldDir"
if ($LASTEXITCODE -ne 0) { throw 'BridgeTools build failed' }
Write-Host "Built $(Join-Path $binDir 'Rimlink.BridgeTools.dll')"
if ($NoInstall) { return }

$target = Join-Path $InstallTo 'Rimlink'
$own = @('Rimlink.BridgeTools.dll', 'Rimlink.BridgeTools.pdb')
if (Test-Path $target) {
    $foreign = Get-ChildItem $target -Force | Where-Object { $own -notcontains $_.Name } | ForEach-Object { $_.Name }
    if ($foreign) {
        throw "Refusing to deploy: $target holds $($foreign -join ', '). The companion folder must contain only Rimlink.BridgeTools.dll (a copy of Rimlink.Client.dll there would load a second, empty session)."
    }
}
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($name in $own) {
    $src = Join-Path $binDir $name
    if (Test-Path $src) { Copy-Item $src $target -Force }
}
Write-Host "Deployed to $target"
