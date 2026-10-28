# Packages a Rimlink release:
#   dist/rimlink-client-<ver>.zip            the mod folder (needs RimWorld to compile against)
#   dist/rimlink-server-<ver>-<rid>.zip      framework-dependent server + rimlink-tools, per runtime
#   .\scripts\Package-Release.ps1 [-RimWorldDir <game>] [-Runtimes linux-x64,win-x64] [-SkipClient] [-SkipServer]
# The version comes from Rimlink.Client.Core/ClientInfo.cs (the single version of the project).

param(
    [string]$RimWorldDir = 'D:\SteamLibrary\steamapps\common\RimWorld',
    [string[]]$Runtimes = @('linux-x64', 'win-x64'),
    [switch]$SkipClient,
    [switch]$SkipServer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$clientInfo = Get-Content (Join-Path $root 'src\Rimlink.Client.Core\ClientInfo.cs') -Raw
if ($clientInfo -notmatch 'Version\s*=\s*"([^"]+)"') { throw 'Version not found in ClientInfo.cs' }
$version = $Matches[1]
Write-Host "Rimlink $version"

if (-not $SkipClient) {
    $modDir = Join-Path $root 'build\mod\Rimlink'
    & (Join-Path $PSScriptRoot 'Build-ClientMod.ps1') -RimWorldDir $RimWorldDir -OutputDir $modDir
    $zip = Join-Path $dist "rimlink-client-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    # The archive root is the mod folder itself, so it unpacks straight into RimWorld/Mods.
    Compress-Archive -Path $modDir -DestinationPath $zip
    Write-Host "  $zip"
}

if (-not $SkipServer) {
    foreach ($rid in $Runtimes) {
        $out = Join-Path $root "build\server\$rid"
        if (Test-Path $out) { Remove-Item -Recurse -Force $out }
        foreach ($project in 'src\Rimlink.Server\Rimlink.Server.csproj', 'src\Rimlink.Tools\Rimlink.Tools.csproj') {
            dotnet publish (Join-Path $root $project) -c Release -r $rid --self-contained false -nologo -v q -o $out
            if ($LASTEXITCODE -ne 0) { throw "publish failed: $project ($rid)" }
        }
        Copy-Item (Join-Path $root 'deploy\appsettings.example.json') $out
        Copy-Item (Join-Path $root 'docs\server-setup.md') $out
        $zip = Join-Path $dist "rimlink-server-$version-$rid.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
        Write-Host "  $zip"
    }
}
