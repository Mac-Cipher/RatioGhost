# Tcl to .NET behavior map

This document is the characterization boundary for the progressive migration. The Tcl application remains available and is not replaced by the .NET executable yet.

| Tcl responsibility | Current Tcl location | .NET destination | Milestone status |
| --- | --- | --- | --- |
| Settings defaults, profile path, backup save | `ghost.tcl:129-250`, `util.tcl:GetProfileDirectory` | `RatioGhost.Core/Configuration` | JSON persistence plus one-time data-only `settings.dat`/`.bak` import; Tcl files remain unchanged |
| Query parsing and case-insensitive rewrites | `proxy.tcl:533-635` | `RatioGhost.Core/Announcements/QueryStringEditor` | Implemented and characterized |
| Actual/reported torrent counters | `proxy.tcl:129-186` | `AnnounceTransformer` | First vertical slice implemented |
| FreeLeech and pretend-seed options | `proxy.tcl:273-291` | `AnnounceTransformer` | Implemented, characterized, and compared through both the live Tcl functions and a completed-event network replay |
| Peer threshold, multipliers, boost, pause consistency | `proxy.tcl:1048-1159` | `AnnounceTransformer` | Implemented and wired to the Avalonia pause control; deterministic tests cover calculation, while live Tcl/.NET replays cover fixed and unequal A/B ratios, boost timing, peer thresholds, and paused counter consistency |
| Tracker response fields | `proxy.tcl:1340-1360` | `TrackerResponseParser` | Implemented and tested with fragmented-equivalent payloads |
| HTTP listener and forwarding | `proxy.tcl:52-126`, `958-1261` | `RatioGhost.Proxy/HttpProxyServer` | Async localhost GET proxy implemented and integration-tested |
| CONNECT policy and TLS interception | `proxy.tcl:480-749` | `RatioGhost.Proxy` plus `ICertificateAuthorityService` | Windows MITM implemented for port 443; unavailable/untrusted CA fails closed |
| Strict outbound TLS validation | `util.tcl:TlsClientOptions`, `proxy.tcl:1208-1250` | `SocketsHttpHandler` | Implemented with the system trust store, hostname validation, and no validation override |
| Redacted proxy debug log | `proxy.tcl:dlog`, `proxy.tcl:197-242` | `IProxyDebugLogger` and `FileProxyDebugLogger` | Opt-in rotating `proxy_debug.log`; tracker secrets and token-like path segments are redacted before custom or file sinks |
| GUI, options, activity log | `gui.tcl` | `RatioGhost.Desktop` | Essential Avalonia UI implemented |
| Torrent state and counters | `gui.tcl:507-804`, `proxy.tcl:129-186` | `TorrentSnapshot` and Avalonia Torrents tab | Current/accumulated actual and reported counters, seeds/leechers, status, copy-hash, and confirmed per-torrent reset implemented |
| Tray | `gui.tcl:132-226` | Avalonia `TrayIcon` | Windows tray menu and runtime path are implemented/tested; non-Windows tray backends remain deferred until native integration is validated, `StartMinimized` and the hide control are ignored there, and closing the only window exits cleanly so the process cannot become unreachable |
| Autostart | `ghost.tcl:81-125` | `IAutostartService` | Windows per-user Run-key, Linux XDG desktop entry, and macOS LaunchAgent file services are collision-safe and tested with injected paths; native macOS session launch remains unverified |
| OS certificate trust | generated leaf certificate in Tcl | `ICertificateAuthorityService` | Windows CurrentUser CA implemented; explicit enable/remove actions; macOS/Linux pending |

## Compatibility rules

- Preserve query parameter spelling, ordering, duplicate occurrences, encoding, and fragments where possible.
- Treat `info_hash` presence case-insensitively, as the hardened Tcl implementation does.
- Block non-tracker traffic when tracker-only mode is enabled.
- Never allow an uploaded counter to regress below the last reported value.
- Keep HTTP and HTTPS behavior under dual-implementation comparison until real network/TLS tests pass.
- Never install a certificate without an explicit user action and an OS-specific removal path.
