[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist"),
    [string[]]$Runtime = @(
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    )
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "CodexHistoryFixer.csproj"
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

foreach ($rid in $Runtime) {
    $publishDirectory = Join-Path $outputRoot $rid
    Write-Host "Publishing $rid -> $publishDirectory"

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --output $publishDirectory `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $rid failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Release files are available in $outputRoot"
