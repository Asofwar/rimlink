param(
    [Parameter(Mandatory)][string]$EvidenceRecord,
    [Parameter(Mandatory)][ValidateSet('Candidate', 'Seal')][string]$Action,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $taskRoot 'tools/Rimlink.OutpostAcceptanceHost/Rimlink.OutpostAcceptanceHost.csproj'
# Rebuild first: any changed safety source invalidates records for the previous identity.
& dotnet build $project -c Release -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$step = if ($Action -eq 'Seal') { 'build-seal' } else { 'build-candidate' }
& dotnet run --project $project -c Release --no-build -- $step ([System.IO.Path]::GetFullPath($EvidenceRecord)) ([System.IO.Path]::GetFullPath($OutputPath))
exit $LASTEXITCODE
