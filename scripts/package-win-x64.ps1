[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64'),
    [string]$ArchivePath = (Join-Path $PSScriptRoot '..\artifacts\RatioGhost-dotnet-win-x64.zip')
)

$ErrorActionPreference = 'Stop'

$publishScript = Join-Path $PSScriptRoot 'publish-win-x64.ps1'
& $publishScript -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $OutputDirectory 'RatioGhost.exe'
$executableHashFile = Join-Path $OutputDirectory 'RatioGhost.exe.sha256'
if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath $executableHashFile)) {
    throw 'The published executable or its checksum file is missing.'
}

$archiveDirectory = Split-Path -Parent $ArchivePath
if (![string]::IsNullOrWhiteSpace($archiveDirectory)) {
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
}
Compress-Archive -LiteralPath @($executable, $executableHashFile) -DestinationPath $ArchivePath -Force

$archiveHashPath = "$ArchivePath.sha256"
$archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$archiveName = Split-Path -Leaf $ArchivePath
"$archiveHash  $archiveName" |
    Set-Content -LiteralPath $archiveHashPath -Encoding ascii

$actualExecutableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$declaredExecutableHash = ((Get-Content -LiteralPath $executableHashFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ($actualExecutableHash -ne $declaredExecutableHash) {
    throw "Published EXE checksum mismatch: expected $declaredExecutableHash, got $actualExecutableHash."
}

$declaredArchiveHash = ((Get-Content -LiteralPath $archiveHashPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ($archiveHash -ne $declaredArchiveHash) {
    throw "Published ZIP checksum mismatch: expected $declaredArchiveHash, got $archiveHash."
}

$entries = @(tar -tf $ArchivePath)
if (($entries -notcontains 'RatioGhost.exe') -or ($entries -notcontains 'RatioGhost.exe.sha256')) {
    throw "Published ZIP entries are incomplete: $($entries -join ', ')."
}

Write-Output "Published EXE: $executable"
Write-Output "EXE SHA256: $actualExecutableHash"
Write-Output "Archive: $ArchivePath"
Write-Output "ZIP SHA256: $archiveHash"
Write-Output "ZIP entries: $($entries -join ', ')"
