#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\artifacts\win-x64\RatioGhost.exe'),
    [ValidateRange(5, 300)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'The Windows package smoke can only run on Windows.'
}

$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (-not [IO.File]::Exists($executable)) {
    throw "Published executable was not found: $executable"
}

function Get-RatioGhostThumbprints {
    param([string]$StorePath)

    @(
        Get-ChildItem $StorePath -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like '*RatioGhost*' } |
            Select-Object -ExpandProperty Thumbprint
    )
}

function Reserve-LoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Remove-GeneratedProfile {
    param([string]$Path)

    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($tempRoot, $fullPath)
    if ([IO.Path]::IsPathRooted($relative) -or
        $relative -notmatch '^RatioGhost-Package-Smoke-[0-9a-f]{32}$') {
        throw "Refusing to remove a profile outside the generated smoke namespace: $fullPath"
    }

    if ([IO.Directory]::Exists($fullPath)) {
        [IO.Directory]::Delete($fullPath, $true)
    }
}

$port = Reserve-LoopbackPort
$profile = Join-Path $env:TEMP ('RatioGhost-Package-Smoke-' + [Guid]::NewGuid().ToString('N'))
$oldPort = $env:RATIOGHOST_LISTEN_PORT
$oldProfile = $env:RATIOGHOST_PROFILE_DIR
$beforeRoot = Get-RatioGhostThumbprints 'Cert:\CurrentUser\Root'
$beforeMy = Get-RatioGhostThumbprints 'Cert:\CurrentUser\My'
$process = $null

try {
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    $env:RATIOGHOST_LISTEN_PORT = [string]$port
    $env:RATIOGHOST_PROFILE_DIR = $profile
    $process = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $listening = $false
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "RatioGhost exited during smoke with code $($process.ExitCode)."
        }

        $listening = $null -ne (
            Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -eq $process.Id } |
                Select-Object -First 1)
    } until ($listening -or [DateTime]::UtcNow -ge $deadline)

    if (-not $listening) {
        throw "RatioGhost did not listen on the isolated port $port."
    }

    $settingsPath = Join-Path $profile 'settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        throw 'RatioGhost did not persist its isolated settings.'
    }

    Write-Output "Package smoke passed: PID $($process.Id), port $port."
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.WaitForExit(10000) | Out-Null
    }

    $env:RATIOGHOST_LISTEN_PORT = $oldPort
    $env:RATIOGHOST_PROFILE_DIR = $oldProfile

    $afterRoot = Get-RatioGhostThumbprints 'Cert:\CurrentUser\Root'
    $afterMy = Get-RatioGhostThumbprints 'Cert:\CurrentUser\My'
    if ((@($beforeRoot | Sort-Object) -join '|') -ne (@($afterRoot | Sort-Object) -join '|')) {
        throw 'Package smoke changed CurrentUser\Root.'
    }
    if ((@($beforeMy | Sort-Object) -join '|') -ne (@($afterMy | Sort-Object) -join '|')) {
        throw 'Package smoke changed CurrentUser\My.'
    }

    Remove-GeneratedProfile $profile
}
