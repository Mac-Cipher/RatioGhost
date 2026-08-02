# Progressive .NET migration

## Current milestone

The repository now contains a side-by-side .NET 10 implementation:

- `RatioGhost.Core`: platform-neutral settings, query handling, tracker response parsing, and announce transformation.
- `RatioGhost.Proxy`: platform-neutral asynchronous HTTP proxy with bounded headers, connection limits, timeouts, strict default outbound TLS behavior, and opt-in redacted rotating diagnostics.
- `RatioGhost.Desktop`: Avalonia 12 desktop UI, Windows tray integration, persistent configuration, and platform service abstractions.
- `tests-dotnet`: deterministic unit tests and real TCP integration tests using a local fake tracker.

On first start, if `settings.json` does not exist, the .NET application parses the legacy Tcl `settings.dat` as data (it never evaluates Tcl code), validates known fields, writes an atomic JSON configuration, and leaves `settings.dat` and its backup unchanged. A valid `settings.dat.bak` is used only when the primary legacy file is invalid.

Windows is the first packaged target (`scripts/publish-win-x64.ps1`). Core, Proxy, and the Desktop test assembly run in CI on Windows, Linux, and macOS. The cross-platform Desktop tests verify the platform capability boundary: Windows has the per-user Run-key service, Linux has a collision-safe XDG autostart entry tested under Ubuntu WSL, macOS has a collision-safe LaunchAgent file service, and certificate trust remains explicitly unsupported outside Windows. Windows-only certificate, Tcl, and qBittorrent cases execute only on Windows. Only Windows receives the self-contained package and a launched-process/listener smoke test. Tagged releases keep the Tcl executable and add a distinct `RatioGhost-dotnet-win-x64.zip` plus checksum, so the migration does not silently replace the reference implementation. Cross-platform build/test coverage validates the dependency boundary; it does not claim that the desktop product is supported on macOS or Linux.

The pinned SDK (`global.json` and CI: .NET 10.0.302) compiles the Desktop project for `win-x64`, `linux-x64`, and `osx-x64` in the corresponding CI matrix. The Desktop project declares all three `RuntimeIdentifiers`, so one normal restore prepares the assets needed for subsequent no-restore RID builds; the same `linux-x64`/`osx-x64` targets have been compiled from Windows with zero warnings or errors. Those RID builds are compile-time portability evidence only; native window, tray, certificate-store, and session integration still require runtime validation on their respective operating systems.

A local Ubuntu 20.04 WSL run with the pinned .NET 10.0.302 SDK independently passed Core 20/20, Proxy 12/12, and Desktop 20/20, including the two Linux XDG autostart tests; that recorded run predates the later Windows-only desktop-surface and macOS LaunchAgent tests. The Desktop result includes the explicit non-Windows capability boundary; Windows-only certificate, Tcl, qBittorrent, and packaged-UI cases intentionally do not execute there, so this is Linux build/core/proxy plus autostart-service evidence rather than a Linux desktop or HTTPS support claim. The current Windows Desktop assembly has 37 tests, including portable CA issuance/chain coverage, native Avalonia construction, collision-safe Linux XDG and macOS LaunchAgent service/DTD-safety coverage, Platform/HTTPS controls, tray menu entries, cancellation-safe CA cleanup coverage, cancellation-safe CA reads, cancellation-safe autostart disable coverage, corruption-safe CA metadata failure coverage, last-moment Linux XDG and LaunchAgent collision protection, and strict qBittorrent rejection of an untrusted MITM certificate.

Both configured WSL distributions on the current workstation now report `dotnet` unavailable, so a fresh Linux runtime test was not possible in this continuation; the WSL result above is retained as historical evidence, while the current RID builds remain compile-time evidence only.

The current Windows solution run passes Core 22/22, Proxy 15/15, and Desktop 37/37. Core coverage also verifies that an interrupted atomic JSON save leaves the prior configuration intact and removes its temporary file, and that an explicit profile override is normalized to an absolute path. The CA service now treats malformed metadata as a fail-closed state rather than generating a replacement key that could orphan the previous installation certificate.

The imported Tcl `proxy_debug_logging` setting is now functional in .NET. The Options tab exposes it explicitly; when enabled, the proxy writes a bounded rotating `proxy_debug.log` under the profile directory. Redaction happens before a custom logger receives the event and again in the file sink, covering tracker credentials, peer identifiers, network-address query keys, and long token-like path segments. Logging failures are swallowed so diagnostics cannot interrupt proxy traffic.

The Avalonia surface now treats the tray as an explicit capability: the Windows-only hide control is not offered when no tray backend is available, and closing the sole non-Windows window performs the normal asynchronous shutdown instead of hiding an unreachable process. This keeps the cross-platform shell usable while native tray/session integration remains pending.

## HTTPS status

The Windows milestone supports HTTPS tracker announcements through `CONNECT :443` after the user explicitly checks the CA consent box and selects **Enable HTTPS interception**.

The implementation:

1. keeps the installation CA private key in the Windows `CurrentUser\My` certificate store as a non-exportable key;
2. installs the public root into `CurrentUser\Root` only after explicit consent;
3. generates and caches 30-day per-host leaf certificates with DNS/IP SAN entries;
4. removes the trusted root, private CA, cached leaves, and local metadata through **Remove CA trust**;
5. uses the normal .NET system trust and hostname validation for outbound tracker TLS;
6. binds absolute HTTPS request targets to the original `CONNECT` authority;
7. fails closed when the CA is unavailable or not trusted.

Automated coverage performs a real client-to-proxy TLS handshake, validates the generated chain, verifies the decrypted announcement rewrite, rejects an untrusted upstream tracker certificate, and fails closed on malformed or incomplete `CONNECT` requests. The CA lifecycle and trust decisions are tested against an isolated certificate-store implementation. A separate Windows integration test verifies creation of the non-exportable private CA in `CurrentUser\My` and exact cleanup without adding anything to `CurrentUser\Root`; Windows deliberately displays a security confirmation for root installation, so that protected user-consent dialog is not automated in CI.

The Windows desktop suite also launches the installed qBittorrent against disposable `--profile` and `--configuration` directories, adds synthetic one-byte torrents, and observes both HTTP and HTTPS tracker announcements through an in-process RatioGhost proxy. The HTTPS case performs a real `CONNECT :443` exchange with an ephemeral test CA and verifies that the TLS tracker receives `left=0`. It disables qBittorrent's tracker-certificate validation only inside the disposable test profile, so it proves the qBittorrent proxy protocol and decrypted rewrite path but does not prove the final client-side Windows trust-store experience. RatioGhost's outbound connection still validates the synthetic tracker against a strict, test-only root and rejects hostname mismatch. Both tests preserve the real qBittorrent configuration byte-for-byte, never open the user's torrent profile, and stop their isolated client without modifying Windows certificate stores.

An isolated attempt to turn qBittorrent's `Session\ValidateHTTPSTrackerCertificate` on while supplying the ephemeral PEM through `SSL_CERT_FILE` did not reach the tracker and timed out; the Windows client did not consume that bundle in this setup. The experiment was reverted. The current installed-client smoke instead proves the negative strict-validation path without changing trust configuration. The attended Windows trust-store round trip has now also passed 1/1: it installed the installation-specific CA in `CurrentUser\Root`, validated a generated leaf chain, and removed the exact CA from both stores. That proves the protected Windows store lifecycle, but not qBittorrent's positive client validation path.

These qBittorrent tests run only on a Windows machine where qBittorrent is installed. CI runners without qBittorrent still execute all deterministic Core, Proxy, TLS, certificate-store, and packaging checks but do not constitute qBittorrent interoperability proof.

The installed-client smokes are intentionally isolated interoperability checks. On this workstation, two earlier full Desktop-assembly runs intermittently timed out one qBittorrent child before its first announce (the failing protocol alternated between HTTP and HTTPS); each positive qBittorrent smoke now permits one bounded retry only for a startup cancellation, while protocol assertions remain fail-fast. The three qBittorrent tests pass together (3/3), and together with the three Tcl network replays (6/6). The strict-validation case sets `Session\ValidateHTTPSTrackerCertificate=true` against the untrusted ephemeral MITM CA and observes no tracker HTTP request; it proves rejection, not the positive trusted-root path. After removing an unused strict-test `SSL_CERT_FILE` override from the positive path, the latest full Windows Desktop run passed 37/37. The earlier environment-sensitive result remains visible here so a future recurrence is not mistaken for a product regression.

Each qBittorrent smoke now deletes its generated GUID directory after the child process and proxy stop; Windows profile-lock release is handled with a bounded retry. This keeps disposable profiles and torrent fixtures out of `%TEMP%` after a successful or failed attempt without touching the user's qBittorrent configuration.

Windows characterization now has four levels. A headless oracle executes the current Tcl query-rewrite functions through `tclkitsh.exe` and compares their deterministic free-leech/pretend-seed result with `AnnounceTransformer`. A second test launches the live Tcl/Tk proxy source through `tclkit.exe` with isolated ports and `APPDATA`, sends three real HTTP announces through it, feeds back a five-leecher tracker response, and compares the fixed-ratio rewrites byte-for-byte with .NET. It proves both the second announce (`uploaded=540`) and a completed freeleech/pretend-seed announce (`uploaded=800`, `downloaded=0`, `left=0`, completed event removed). The isolated Tcl mode also bypasses autostart synchronization so the user's registry entry is never read or changed.

A third live replay starts the same Tcl proxy with the isolated `RATIOGHOST_ISOLATED_PAUSED=1` hook and compares a two-announce counter-regression sequence. Both runtimes preserve the previously reported `downloaded=100` and `uploaded=200` values while paused; the hook cannot be enabled by normal application settings. A fourth replay injects a deterministic random sequence and elapsed time through isolated-only hooks, then compares unequal A/B ratios and a 100% boost chance (`uploaded=3944`) byte-for-byte. Normal Tcl execution still uses its native `rand()` and clock paths.

Remaining before HTTPS is declared production-compatible:

- an attended Windows test of the packaged UI consent together with qBittorrent validation enabled and the **Remove CA trust** path (the lower-level protected root-store round trip is now validated 1/1);
- macOS Keychain and Linux trust-store implementations and tests (Linux XDG and macOS LaunchAgent file services are implemented with injected-path tests, but native tray/session launch and trust-store integration remain unverified).

## Verification matrix

| Requirement | Current evidence | Status and boundary |
| --- | --- | --- |
| Preserve the Tcl reference behavior | `tclkitsh.exe tests/all.tcl` (46/46), deterministic Tcl/.NET oracles, and live isolated Tcl network replays | Complete for the characterized behavior; Tcl remains the reference implementation |
| Pure transformation and proxy layers | Core 22/22, Proxy 15/15, real HTTP/TLS integration tests | Complete for the current vertical slice |
| Windows desktop milestone | Avalonia surface/tray tests, `scripts/smoke-win-x64.ps1` launched self-contained `win-x64` smoke, persistent isolated settings | Complete and packaged; native interactive tray testing remains represented by the launched-process smoke |
| Windows package integrity | EXE/ZIP checksums, required ZIP entries, CI verification step | Complete for the current package workflow |
| Local CA and strict outbound TLS | Isolated CA lifecycle/chain tests, strict outbound handler tests, fail-closed CONNECT behavior, and attended `CurrentUser\Root` round trip (1/1) | Complete for the Windows store lifecycle; packaged UI consent and qBittorrent client validation remain unverified |
| qBittorrent interoperability | Disposable HTTP and HTTPS profiles, real announce rewrites, bounded startup retry, and strict-validation rejection against an untrusted MITM CA | Protocol path and negative strict-validation path proven; positive client trust remains unverified |
| macOS/Linux architecture | RID builds, injected Linux XDG and macOS LaunchAgent file tests, explicit unsupported trust capability | Compile/file integration only; native session, tray, and trust-store runtime support remain unverified |
| User data and working tree safety | Legacy files/config hashes unchanged, no automatic Root install, uncommitted changes preserved | Complete for the migration operations performed here |

## Local commands

```powershell
.\tclkitsh.exe .\tests\all.tcl
dotnet test .\tests-dotnet\RatioGhost.Core.Tests\RatioGhost.Core.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Proxy.Tests\RatioGhost.Proxy.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Desktop.Tests\RatioGhost.Desktop.Tests.csproj -c Release
dotnet build .\RatioGhost.slnx -c Release
.\scripts\publish-win-x64.ps1
.\scripts\package-win-x64.ps1
.\scripts\smoke-win-x64.ps1
```

The protected Windows root-store round trip is intentionally opt-in and attended:

```powershell
$env:RATIOGHOST_RUN_ATTENDED_TRUST_TEST = '1'
$env:RATIOGHOST_ATTENDED_PROFILE = Join-Path $env:TEMP 'RatioGhost-Attended-Trust'
dotnet test .\tests-dotnet\RatioGhost.Desktop.Tests\RatioGhost.Desktop.Tests.csproj `
  -c Release --filter 'FullyQualifiedName~TrustRoundTrip_CurrentUserRootStore'
```

Windows shows its normal security dialog. Verify the installation-specific `RatioGhost Local CA` identity before selecting **Yes**. The test verifies the generated leaf chain and removes that exact CA from `CurrentUser\Root` and `CurrentUser\My` in `finally`. Do not run it unattended.

The same filtered test was compiled and run on Windows with the opt-in variable unset: 1/1 passed by taking the guarded no-op path, with no Root/My certificate added and no test process left behind. CI repeats this disabled-mode guard on Windows. The attended wrapper was subsequently run with explicit consent (`INSTALL`) and passed 1/1; it restored the environment and verified that the RatioGhost entries in both stores were unchanged after cleanup.

The wrapper `scripts/run-attended-trust-test.ps1` adds a second safety gate: it refuses to run without `-ConfirmTrustPrompt`, then requires the operator to type `INSTALL`, uses a unique profile by default, restores the opt-in environment variables, and verifies that the RatioGhost entries in `CurrentUser\Root` and `CurrentUser\My` are unchanged after the test. Creating this wrapper does not install or trust a certificate; the attended run described above used it explicitly.

To exercise that wrapper without opening a trust dialog or enabling the certificate-store path, run `.\scripts\run-attended-trust-test.ps1 -DryRun`. This clears the opt-in flag only for the filtered child test, restores the caller's environment, and verifies that both stores remain unchanged; the current Windows run passed 1/1. A real attended run must still be initiated deliberately with `-ConfirmTrustPrompt`; the wrapper then requires the literal `INSTALL` confirmation.

The legacy Tcl executable remains the production reference while the two implementations are compared.
