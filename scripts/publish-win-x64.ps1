[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\RatioGhost.Desktop\RatioGhost.Desktop.csproj'

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $OutputDirectory 'RatioGhost.exe'
if (!(Test-Path -LiteralPath $executable)) {
    throw "Expected executable was not produced: $executable"
}

$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  RatioGhost.exe" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'RatioGhost.exe.sha256') -Encoding ascii
Write-Host "Published $executable"
