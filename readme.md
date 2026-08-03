# 👻 Ratio Ghost

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-8B5CF6.svg)](https://avaloniaui.net/)
[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-green.svg)](license.txt)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](#)

Translations: [🇬🇧 English](readme.md) | [🇫🇷 Français](README.fr.md)

Ratio Ghost is a local HTTP/HTTPS proxy that rewrites the announces sent by your BitTorrent client to private trackers.

The application is written in C#/.NET 10 and uses Avalonia for its desktop interface. The proxy is bound to `localhost` and only intercepts tracker traffic.

## Features

- Configurable upload and download counter rewriting.
- FreeLeech mode and Pretend to Seed.
- Leecher threshold, multipliers, upload boost, and rewrite pause.
- HTTP proxy and HTTPS interception with explicit consent.
- Installation-specific CA and per-host certificates.
- Avalonia interface, activity log, Windows tray, and autostart.
- Optional diagnostic logging with tracker identifiers redacted.

## Download for Windows

Published releases contain the self-contained .NET archive:

1. Open the [Releases](https://github.com/Mac-Cipher/RatioGhost/releases) page.
2. Download `RatioGhost-dotnet-win-x64.zip` and its `.sha256` file.
3. Verify the checksum before extracting the archive:

   ```powershell
   Get-FileHash .\RatioGhost-dotnet-win-x64.zip -Algorithm SHA256
   Get-Content .\RatioGhost-dotnet-win-x64.zip.sha256
   ```

4. Extract the archive and run `RatioGhost.exe`.

Enabling HTTPS requires confirmation in the **Platform** tab and then in the Windows security dialog. The officially packaged target is `win-x64`.

## Run from source

Prerequisite: .NET SDK `10.0.302`.

```powershell
dotnet restore .\RatioGhost.slnx
dotnet run --project .\src\RatioGhost.Desktop\RatioGhost.Desktop.csproj
```

To route torrent tracker announces through Ratio Ghost:

1. Configure an HTTP proxy at `127.0.0.1:3773`.
2. Enable hostname resolution through the proxy.
3. Do not use Ratio Ghost as a peer-to-peer proxy.

For qBittorrent, enable the proxy for tracker communications and leave peer-to-peer connections disabled.

Ratio Ghost does not hide peer-to-peer traffic or modify connections between peers.

## Configuration migration

The current configuration is stored as JSON in the Ratio Ghost profile. If `settings.json` does not exist yet, the application can import an existing `settings.dat` as data only, without executing code, and then write the JSON configuration. The original files are left unchanged.

## Development and verification

```powershell
dotnet test .\tests-dotnet\RatioGhost.Core.Tests\RatioGhost.Core.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Proxy.Tests\RatioGhost.Proxy.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Desktop.Tests\RatioGhost.Desktop.Tests.csproj -c Release
dotnet build .\RatioGhost.slnx -c Release
.\scripts\package-win-x64.ps1
.\scripts\smoke-win-x64.ps1
```

The Windows trust test is intentionally opt-in and displays a security dialog. Run it only with explicit consent.

## Architecture

- `src/RatioGhost.Core`: configuration, parsing, and announce transformation.
- `src/RatioGhost.Proxy`: asynchronous HTTP/HTTPS proxy, network limits, and redacted logging.
- `src/RatioGhost.Desktop`: Avalonia interface, Windows tray, certificates, and autostart.
- `tests-dotnet`: unit, network integration, and packaging tests.
- `scripts`: Windows publishing, packaging, and smoke tests.
- `assets`: application resources.

The repository contains one application implementation: .NET/Avalonia. Linux and macOS builds validate platform boundaries and compilation; the official desktop distribution remains Windows.

## License

Distributed under the GNU General Public License v3. See [`license.txt`](license.txt).
