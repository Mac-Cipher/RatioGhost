using RatioGhost.Desktop.Platform;

namespace RatioGhost.Desktop.Tests;

public sealed class PlatformServicesTests
{
    [Fact]
    public async Task Factories_ReportOnlyImplementedCapabilities()
    {
        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.PlatformTests",
            Guid.NewGuid().ToString("N"));
        var autostart = PlatformServices.CreateAutostart();
        var certificates = PlatformServices.CreateCertificateAuthority(profile);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.True(autostart.Capability.IsSupported);
                Assert.True(certificates.Capability.IsSupported);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                Assert.True(autostart.Capability.IsSupported);
                Assert.Contains("XDG", autostart.Capability.Description);
                Assert.False(certificates.Capability.IsSupported);
                Assert.False(await certificates.IsTrustedAsync());
                using var linuxCanceled = new CancellationTokenSource();
                linuxCanceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => certificates.IsTrustedAsync(linuxCanceled.Token));
                await Assert.ThrowsAsync<PlatformNotSupportedException>(
                    () => certificates.RequestTrustAsync());
                await Assert.ThrowsAsync<PlatformNotSupportedException>(
                    () => certificates.GetServerCertificateAsync("tracker.test"));
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Assert.True(autostart.Capability.IsSupported);
                Assert.Contains("LaunchAgent", autostart.Capability.Description);
                Assert.False(certificates.Capability.IsSupported);
                Assert.False(await certificates.IsTrustedAsync());
                using var macCanceled = new CancellationTokenSource();
                macCanceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => certificates.IsTrustedAsync(macCanceled.Token));
                await Assert.ThrowsAsync<PlatformNotSupportedException>(
                    () => certificates.RequestTrustAsync());
                await Assert.ThrowsAsync<PlatformNotSupportedException>(
                    () => certificates.GetServerCertificateAsync("tracker.test"));
                return;
            }

            Assert.False(autostart.Capability.IsSupported);
            Assert.False(certificates.Capability.IsSupported);
            Assert.False(await autostart.IsEnabledAsync());
            Assert.False(await certificates.IsTrustedAsync());
            using var unsupportedCanceled = new CancellationTokenSource();
            unsupportedCanceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => autostart.IsEnabledAsync(unsupportedCanceled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => certificates.IsTrustedAsync(unsupportedCanceled.Token));
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => autostart.SetEnabledAsync(true));
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => certificates.RequestTrustAsync());
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => certificates.GetServerCertificateAsync("tracker.test"));
        }
        finally
        {
            if (certificates is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
