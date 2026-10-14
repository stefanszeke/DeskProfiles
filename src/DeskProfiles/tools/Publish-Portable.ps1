param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\DeskProfiles.csproj'
$output = Join-Path $PSScriptRoot "..\publish\$Runtime"
if ($Runtime -notin @('win-x64','win-arm64')) { throw 'Runtime must be win-x64 or win-arm64.' }
dotnet publish $project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host "Portable DeskProfiles build: $output"
Write-Host 'Exit the dev copy completely before launching the portable EXE.'
