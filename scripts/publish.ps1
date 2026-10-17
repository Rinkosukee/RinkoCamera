param(
    [string]$Configuration = "Release",
    [string]$Output = "artifacts\publish\RinkoCamera\win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\RinkoCamera\RinkoCamera.csproj"
$outputPath = Join-Path $root $Output

dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true --property:PublishSingleFile=false --property:PublishTrimmed=false --output $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published to $outputPath"
