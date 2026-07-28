# 👻 Ratio Ghost

[![Tcl/Tk Version](https://img.shields.io/badge/Tcl%2FTk-8.6-blue.svg)](http://tcl.tk/)
[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-green.svg)](license.txt)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](#)

Translations: [🇬🇧 English](readme.md) | [🇫🇷 Français](README.fr.md)

Ratio Ghost is a lightweight, local HTTP/HTTPS intercepting proxy designed to automatically modify and improve your BitTorrent ratio reported to private trackers.

Written in Tcl/Tk, it acts as a man-in-the-middle proxy between your BitTorrent client (e.g., uTorrent, qBittorrent) and the tracker, seamlessly adjusting your upload and download statistics on the fly.

---

## ✨ Key Features

- **Smart Ratio Manipulation**:
  - Dynamically calculates reported upload based on configurable multipliers.
  - Custom upload multipliers for different peer count scenarios (avoids flags on low-leecher torrents).
  - Add artificial speed boosts (e.g., random spikes of 0 to $X$ KB/s with a configurable chance).
- **Stealth Options**:
  - **FreeLeech Mode**: Report download amounts as zero while still gaining upload credits.
  - **Pretend to Seed**: Mark download status as complete (zero bytes left) to appear as a seeder immediately.
- **Security & Scope**:
  - Limit connections to localhost (`127.0.0.1`) only to prevent external access.
  - Intercept only tracker traffic (ignores regular HTTP/HTTPS web requests).
- **Windows & Portable**:
  - Tested and released for Windows.
  - Builds into a single `.exe` binary with its TLS trust store included.

---

## 🛠️ Download & Installation

### 1. Pre-built Executable (Windows)
To run Ratio Ghost without any installation or compilation:

1. Go to the [Releases](https://github.com/Mac-Cipher/RatioGhost/releases) page.
2. Download the **`ratioghost.exe`** file from the latest version.
3. Download `ratioghost.exe.sha256` and verify the executable before launching it:
   ```powershell
   Get-FileHash .\ratioghost.exe -Algorithm SHA256
   Get-Content .\ratioghost.exe.sha256
   ```
4. Double-click the verified executable to launch the application.

> [!NOTE]
> If SmartScreen warns about the unsigned executable, run it only after the SHA-256 matches the checksum published with the GitHub release.

### 2. Running From Source
The supported source-development environment is Windows with Tcl/Tk 8.6 and the native libraries included in this repository. Linux and macOS are not currently built or tested by CI.

Open a terminal in the project directory and run:
```bash
wish rghost.vfs/main.tcl
```

---

## ⚙️ Torrent Client Configuration

To route your torrent tracker announces through Ratio Ghost:

1. Launch **Ratio Ghost**. It will listen locally on:
   - **HTTP Port**: `3773`
   - **HTTPS Port**: `3774`
2. Open your torrent client settings/preferences.
3. Go to **Connection** or **Proxy Server** settings.
4. Set the Proxy type to **HTTP**.
5. Set Host/Address to `127.0.0.1` and Port to `3773`.
6. Enable the option: *"Use proxy for hostname lookups"* (or *"Use proxy for peer-to-peer connections"* if your tracker requires it, although Ratio Ghost is designed to intercept tracker requests, not peer connections).

#### 🔹 qBittorrent Configuration Details
1. Open qBittorrent and go to **Tools** -> **Options** (or press `Alt + O`).
2. Click on the **Connection** tab in the left panel.
3. Scroll down to the **Proxy Server** section:
   - **Type**: Select HTTP.
   - **Host/Address**: Enter 127.0.0.1.
   - **Port**: Always enter `3773`, including for HTTPS trackers (`CONNECT` is intercepted locally so tracker announces can be adjusted).
4. Ensure the following options are set:
   - **Use proxy for peer connections**: ❌ *Leave unchecked* (Ratio Ghost is not a peer proxy, routing peer data will fail).
   - **Use proxy for torrent transmission**: Check this! (Required to route tracker announces through the proxy).
   - **Perform hostname lookups via proxy**: Check this.
5. Click **Apply** and **OK**.

#### 🔹 uTorrent / BitTorrent Configuration Details
1. Open uTorrent and go to **Options** -> **Preferences** (or press `Ctrl + P`).
2. Click on the **Connection** tab in the left panel.
3. Locate the **Proxy Server** section:
   - **Type**: Select HTTP.
   - **Proxy**: Enter 127.0.0.1.
   - **Port**: Enter 3773.
4. Ensure the following options are set:
   - **Use proxy for peer-to-peer connections**: ❌ *Leave unchecked*.
   - **Resolve hostnames through proxy**: Check this.
   - **Use proxy for tracker communications**: Check this.
5. Click **Apply** and **OK**.

> [!IMPORTANT]
> Ratio Ghost modifies HTTP and HTTPS tracker announces. HTTPS is decrypted only on localhost, then re-encrypted to the tracker with CA-chain and hostname validation. It does not route or hide peer-to-peer upload/download traffic, so your IP remains visible to peers in the swarm.

> [!WARNING]
> Client-facing HTTPS interception still uses a unique self-signed local certificate. It is compatible with the currently tested qBittorrent setup, but clients that enforce tracker hostname matching may reject it. Keep Ratio Ghost bound to localhost. A future TLS-runtime migration should replace this with a local CA and per-host certificates.

---

## 📖 Usage Instructions

Once Ratio Ghost is running and your torrent client is configured to route through it, you can customize how your ratio is modified:

### 1. General Interface
- **Log Tab**: Displays real-time spoofing activity. Double-click any log line to see detailed connection data and exact intercept values.
- **Options Tab**: Where you configure the ratio-spoofing engine behavior.

### 2. Spoofing Settings (Options Tab)
- **Report download as zero**: (Highly Recommended) Freezes your reported download amount at 0.
- **Pretend to seed**: Marks you as a seeder immediately by reporting 0 bytes left.
- **Leechers Check**: Set the minimum leechers threshold (default is 5). If a torrent has fewer leechers, it reports actual stats to avoid looking suspicious.
- **Multipliers**: Set the random multiplier range for both upload/download (e.g. 4.0 to 8.0 times) which will be added to your reported upload.
- **Upload Boost**: Add a random speed boost (e.g., up to 15 KB/s with a 5% chance) to simulate real activity.

### 3. Background Execution
- **File -> Hide**: Minimizes the application to the Windows System Tray (Systray).
- **File -> Exit**: Exits the application entirely. Alternatively, right-click the system tray icon and choose **Exit**.

---

## 🚀 How it Works

```mermaid
sequenceDiagram
    autonumber
    participant TorrentClient as Torrent Client
    participant RatioGhost as Ratio Ghost (Local Proxy)
    participant Tracker as Torrent Tracker

    TorrentClient->>RatioGhost: Announce request (Upload: 10MB, Download: 5MB)
    Note over RatioGhost: Proxy intercepts request,<br/>calculates spoofed stats<br/>based on options.
    RatioGhost->>Tracker: Announce request (Upload: 45MB, Download: 0MB)
    Tracker-->>RatioGhost: Response (Peer list, tracker stats)
    RatioGhost-->>TorrentClient: Forwarded Response
```

---

## 📦 Building & Packaging (Standalone EXE)

If you have modified the source code in `rghost.vfs/` and want to compile a new standalone Windows executable:

### Prerequisites
Make sure the following files are present in the root folder:
- `tclkit.exe` (32-bit Tcl/Tk runtime to ensure compatibility with native libraries like `Winico`)
- `tclkitsh.exe` (32-bit console runtime)
- `sdx.kit` (Starkit Developer Extension utility)

### Compilation Command
Run the following PowerShell command in the project root:

```powershell
.\tclkitsh.exe sdx.kit wrap ratioghost.exe -runtime .\tclkit.exe -vfs rghost.vfs
```

### Tests

Run the automated Tcl tests before packaging:

```powershell
.\tclkitsh.exe tests\all.tcl
```

Ratio Ghost generates a unique local TLS certificate and private key in the user profile for HTTPS tracker interception. The private key is never shipped in the repository or release executable. Persistent proxy debug logging is disabled by default because tracker URLs can contain private credentials.

> [!TIP]
> **Why 32-bit?**
> The application uses the `Winico` package for Windows system tray integration, which contains a native 32-bit DLL (`Winico06.dll`). Packaging using a 32-bit runtime avoids architecture mismatch crashes on startup.

---

## 📂 Project Architecture

- `rghost.vfs/`: The Virtual File System containing code and assets.
  - `rghost.vfs/main.tcl`: Entrypoint script.
  - `rghost.vfs/lib/app-ghost/ghost.tcl`: Startup, configuration manager, and scheduler.
  - `rghost.vfs/lib/app-ghost/proxy.tcl`: Main intercepting HTTP/HTTPS proxy engine.
  - `rghost.vfs/lib/app-ghost/gui.tcl`: Tk-based user interface.
  - `rghost.vfs/lib/app-ghost/util.tcl`: Helper utilities and formatting functions.
  - `rghost.vfs/lib/app-ghost/update.tcl`: Software update checker.

---

## 📝 License

Distributed under the GNU General Public License v3. See [`license.txt`](license.txt) for more details.


