using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using RatioGhost.Core.Platform;

namespace RatioGhost.Desktop.Platform;

internal static class PlatformServices
{
    public static IAutostartService CreateAutostart() =>
        OperatingSystem.IsWindows()
            ? new WindowsAutostartService()
            : OperatingSystem.IsLinux()
                ? new LinuxAutostartService()
                : OperatingSystem.IsMacOS()
                    ? new MacOsAutostartService()
                : new UnsupportedAutostartService();

    public static ICertificateAuthorityService CreateCertificateAuthority(string profileDirectory) =>
        OperatingSystem.IsWindows()
            ? new WindowsCertificateAuthorityService(profileDirectory)
            : new DeferredCertificateAuthorityService();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAutostartService : IAutostartService
{
    private readonly IWindowsRunKey _runKey;
    private readonly Func<string> _launchCommand;

    public WindowsAutostartService()
        : this(new WindowsRunKey(), ResolveLaunchCommand)
    {
    }

    internal WindowsAutostartService(
        IWindowsRunKey runKey,
        Func<string> launchCommand)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        ArgumentNullException.ThrowIfNull(launchCommand);
        _runKey = runKey;
        _launchCommand = launchCommand;
    }

    public PlatformCapability Capability { get; } =
        new(true, "Windows per-user startup registry entry.");

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_runKey.Read() is not null);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
            _runKey.Write(_launchCommand());
        else
            _runKey.Delete();
        return Task.CompletedTask;
    }

    private static string ResolveLaunchCommand()
    {
        var executable = Environment.ProcessPath ??
                         Process.GetCurrentProcess().MainModule?.FileName ??
                         throw new InvalidOperationException("Cannot resolve the executable path.");
        return $"\"{executable}\" --minimized";
    }
}

internal sealed class UnsupportedAutostartService : IAutostartService
{
    public PlatformCapability Capability { get; } =
        new(false, "Autostart is not implemented or tested on this operating system.");

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(Capability.Description);
    }
}

internal sealed class DeferredCertificateAuthorityService : ICertificateAuthorityService
{
    public PlatformCapability Capability { get; } =
        new(false, "HTTPS MITM is disabled until per-installation CA generation and explicit OS trust are implemented.");

    public Task<bool> IsTrustedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task RequestTrustAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(Capability.Description);
    }

    public Task RemoveTrustAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(Capability.Description);
    }

    public Task<X509Certificate2> GetServerCertificateAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(Capability.Description);
    }
}
