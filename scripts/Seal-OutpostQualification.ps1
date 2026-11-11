param(
    [Parameter(Mandatory)][string]$EvidenceRecord,
    [Parameter(Mandatory)][ValidateSet('Candidate', 'Seal')][string]$Action,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$GuildCapture
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $taskRoot 'tools/Rimlink.OutpostAcceptanceHost/Rimlink.OutpostAcceptanceHost.csproj'
# Rebuild first: any changed safety source invalidates records for the previous identity.
& dotnet build $project -c Release -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$step = if ($Action -eq 'Seal') { 'build-seal' } else { 'build-candidate' }
$arguments = @($step, [System.IO.Path]::GetFullPath($EvidenceRecord), [System.IO.Path]::GetFullPath($OutputPath))
if ($GuildCapture) { $arguments += '--guild-capture' }
& dotnet run --project $project -c Release --no-build -- @arguments
exit $LASTEXITCODE
