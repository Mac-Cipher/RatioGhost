#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$ConfirmTrustPrompt,
    [switch]$DryRun,
    [string]$ProfileDirectory
)

$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'The attended Windows trust test can only run on Windows.'
}

if ($DryRun -and $ConfirmTrustPrompt) {
    throw 'Do not combine -DryRun with -ConfirmTrustPrompt.'
}

if (-not $DryRun -and -not $ConfirmTrustPrompt) {
    throw 'Refusing to start a certificate-store test without -ConfirmTrustPrompt.'
}

if (-not $DryRun) {
    $confirmation = Read-Host 'Type INSTALL to allow the Windows RatioGhost CA trust dialog'
    if ($confirmation -cne 'INSTALL') {
        throw 'Trust test canceled before any certificate-store operation.'
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ProfileDirectory)) {
    $ProfileDirectory = Join-Path $env:TEMP ("RatioGhost-Attended-Trust-$([Guid]::NewGuid().ToString('N'))")
}
$ProfileDirectory = [IO.Path]::GetFullPath($ProfileDirectory)

$previousRunFlag = [Environment]::GetEnvironmentVariable('RATIOGHOST_RUN_ATTENDED_TRUST_TEST')
$previousProfile = [Environment]::GetEnvironmentVariable('RATIOGHOST_ATTENDED_PROFILE')
$beforeRoot = @(
    Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*RatioGhost*' } |
        Select-Object -ExpandProperty Thumbprint
)
$beforeMy = @(
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*RatioGhost*' } |
        Select-Object -ExpandProperty Thumbprint
)

$testFailure = $null
try {
    $runFlag = if ($DryRun) { $null } else { '1' }
    [Environment]::SetEnvironmentVariable('RATIOGHOST_RUN_ATTENDED_TRUST_TEST', $runFlag)
    [Environment]::SetEnvironmentVariable('RATIOGHOST_ATTENDED_PROFILE', $ProfileDirectory)
    Push-Location $repositoryRoot
    try {
        & dotnet test .\tests-dotnet\RatioGhost.Desktop.Tests\RatioGhost.Desktop.Tests.csproj `
            -c Release --filter 'FullyQualifiedName~TrustRoundTrip_CurrentUserRootStore'
        if ($LASTEXITCODE -ne 0) {
            throw "The attended trust test failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
catch {
    $testFailure = $_
}
finally {
    [Environment]::SetEnvironmentVariable('RATIOGHOST_RUN_ATTENDED_TRUST_TEST', $previousRunFlag)
    [Environment]::SetEnvironmentVariable('RATIOGHOST_ATTENDED_PROFILE', $previousProfile)
}

$afterRoot = @(
    Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*RatioGhost*' } |
        Select-Object -ExpandProperty Thumbprint
)
$afterMy = @(
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*RatioGhost*' } |
        Select-Object -ExpandProperty Thumbprint
)

if ((@($beforeRoot | Sort-Object) -join '|') -ne (@($afterRoot | Sort-Object) -join '|') -or
    (@($beforeMy | Sort-Object) -join '|') -ne (@($afterMy | Sort-Object) -join '|')) {
    throw 'The attended trust test changed RatioGhost certificate stores after cleanup.'
}

if ($null -ne $testFailure) {
    throw $testFailure
}

if ($DryRun) {
    Write-Output "Dry-run completed; the opt-in trust test remained disabled and stores were unchanged."
}
else {
    Write-Output "Attended trust test completed; stores unchanged. Isolated profile: $ProfileDirectory"
}
