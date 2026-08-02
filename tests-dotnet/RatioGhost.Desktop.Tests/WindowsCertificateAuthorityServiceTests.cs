using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RatioGhost.Desktop.Platform;

namespace RatioGhost.Desktop.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCertificateAuthorityServiceTests
{
    [Fact]
    public async Task TrustCreateLeafAndRemove_RoundTripsIsolatedStore()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        using var service = new WindowsCertificateAuthorityService(profile, store);
        Assert.False(await service.IsTrustedAsync());

        await service.RequestTrustAsync();
        Assert.True(await service.IsTrustedAsync());
        using var metadata = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(profile, "tls", "ca.json")));
        var thumbprint = metadata.RootElement.GetProperty("Thumbprint").GetString() ??
                         throw new InvalidOperationException("CA thumbprint metadata is missing.");
        Assert.Equal(thumbprint, store.PrivateCertificate?.Thumbprint);
        Assert.Equal(thumbprint, store.TrustedCertificate?.Thumbprint);

        using var leaf = await service.GetServerCertificateAsync("tracker.test");
        Assert.True(leaf.HasPrivateKey);
        Assert.Equal("tracker.test", leaf.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(store.TrustedCertificate!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(
            chain.Build(leaf),
            string.Join(", ", chain.ChainStatus.Select(item => item.StatusInformation)));

        await service.RemoveTrustAsync();

        Assert.False(await service.IsTrustedAsync());
        Assert.Null(store.PrivateCertificate);
        Assert.Null(store.TrustedCertificate);
        Assert.False(File.Exists(Path.Combine(profile, "tls", "ca.json")));
    }

    [Fact]
    public async Task CreateLeafAndRemove_RoundTripsCurrentUserPrivateStore()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var service = new WindowsCertificateAuthorityService(profile);
        try
        {
            using var leaf = await service.GetServerCertificateAsync("tracker.test");
            using var metadata = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(profile, "tls", "ca.json")));
            var thumbprint = metadata.RootElement.GetProperty("Thumbprint").GetString() ??
                             throw new InvalidOperationException("CA thumbprint metadata is missing.");
            using var privateStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            privateStore.Open(OpenFlags.ReadOnly);
            var privateRoot = privateStore.Certificates
                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .Single();
            Assert.True(privateRoot.HasPrivateKey);
            Assert.ThrowsAny<Exception>(() => privateRoot.Export(X509ContentType.Pfx));
            Assert.False(await service.IsTrustedAsync());
        }
        finally
        {
            await service.RemoveTrustAsync();
        }
    }

    [Fact]
    public async Task RemoveTrust_RemovesPublicRootWhenPrivateCertificateIsMissing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        string thumbprint;
        using (var creator = new WindowsCertificateAuthorityService(profile, store))
        {
            await creator.RequestTrustAsync();
            thumbprint = store.TrustedCertificate!.Thumbprint;
        }
        store.RemovePrivate(thumbprint);

        using var cleanup = new WindowsCertificateAuthorityService(profile, store);
        Assert.True(await cleanup.IsTrustedAsync());

        await cleanup.RemoveTrustAsync();

        Assert.False(await cleanup.IsTrustedAsync());
        Assert.Null(store.TrustedCertificate);
        Assert.False(File.Exists(Path.Combine(profile, "tls", "ca.json")));
    }

    [Fact]
    public async Task RequestTrust_RemovesOrphanedPublicRootBeforeRegeneration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        string oldThumbprint;
        using (var creator = new WindowsCertificateAuthorityService(profile, store))
        {
            await creator.RequestTrustAsync();
            oldThumbprint = store.TrustedCertificate!.Thumbprint;
        }

        // Simulate an external cleanup that removed only the private key.
        store.RemovePrivate(oldThumbprint);

        using var replacement = new WindowsCertificateAuthorityService(profile, store);
        await replacement.RequestTrustAsync();

        Assert.Contains(
            oldThumbprint,
            store.RemovedTrustedThumbprints,
            StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(oldThumbprint, store.TrustedCertificate!.Thumbprint);
        await replacement.RemoveTrustAsync();
    }

    [Fact]
    public async Task RequestTrust_WhenStoreRejectsRoot_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore { RejectTrustedAdds = true };
        using var service = new WindowsCertificateAuthorityService(profile, store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RequestTrustAsync());

        Assert.Contains("did not add", exception.Message, StringComparison.Ordinal);
        Assert.False(await service.IsTrustedAsync());
        Assert.Null(store.PrivateCertificate);
        Assert.Null(store.TrustedCertificate);
        Assert.False(File.Exists(Path.Combine(profile, "tls", "ca.json")));
        await service.RemoveTrustAsync();
        Assert.Null(store.PrivateCertificate);
    }

    [Fact]
    public async Task RemoveTrust_WhenAlreadyCanceled_DoesNotMutateTheInstallation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        using var service = new WindowsCertificateAuthorityService(profile, store);
        await service.RequestTrustAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RemoveTrustAsync(canceled.Token));

        Assert.True(await service.IsTrustedAsync());
        Assert.NotNull(store.PrivateCertificate);
        Assert.NotNull(store.TrustedCertificate);

        await service.RemoveTrustAsync();
    }

    [Fact]
    public async Task CanceledReads_DoNotCreateOrLoadCertificateMaterial()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        using var service = new WindowsCertificateAuthorityService(profile, store);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.IsTrustedAsync(canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetServerCertificateAsync("tracker.test", canceled.Token));

        Assert.Null(store.PrivateCertificate);
        Assert.Null(store.TrustedCertificate);
        Assert.False(File.Exists(Path.Combine(profile, "tls", "ca.json")));
    }

    [Fact]
    public async Task MetadataFailure_RemovesTheNewPrivateRootAndTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        var tlsDirectory = Path.Combine(profile, "tls");
        Directory.CreateDirectory(tlsDirectory);
        Directory.CreateDirectory(Path.Combine(tlsDirectory, "ca.json"));
        using var store = new IsolatedCertificateStore();
        using var service = new WindowsCertificateAuthorityService(profile, store);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            () => service.GetServerCertificateAsync("tracker.test"));

        Assert.Null(store.PrivateCertificate);
        Assert.False(File.Exists(Path.Combine(tlsDirectory, "ca.json.tmp")));
    }

    [Fact]
    public async Task CorruptMetadata_FailsClosedWithoutGeneratingReplacementCa()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var profile = Path.Combine(
            Path.GetTempPath(),
            "RatioGhost.CertificateTests",
            Guid.NewGuid().ToString("N"));
        using var store = new IsolatedCertificateStore();
        string originalThumbprint;
        using (var creator = new WindowsCertificateAuthorityService(profile, store))
        {
            await creator.RequestTrustAsync();
            originalThumbprint = store.PrivateCertificate!.Thumbprint;
        }

        await File.WriteAllTextAsync(
            Path.Combine(profile, "tls", "ca.json"),
            "{ \"InstallationId\": \"broken\", \"Thumbprint\": \"not-a-thumbprint\" }");

        using var recovery = new WindowsCertificateAuthorityService(profile, store);
        Assert.False(await recovery.IsTrustedAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.GetServerCertificateAsync("tracker.test"));

        Assert.Equal(originalThumbprint, store.PrivateCertificate?.Thumbprint);
        Assert.Equal(originalThumbprint, store.TrustedCertificate?.Thumbprint);
        Assert.DoesNotContain(
            originalThumbprint,
            store.RemovedPrivateThumbprints,
            StringComparer.OrdinalIgnoreCase);

        // The metadata is intentionally left for an explicit repair path; the
        // test removes the exact known certificate pair without touching any
        // real Windows store.
        store.RemoveTrusted(originalThumbprint);
        store.RemovePrivate(originalThumbprint);
    }

    [Fact]
    [Trait("Category", "Attended")]
    public async Task TrustRoundTrip_CurrentUserRootStore_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RATIOGHOST_RUN_ATTENDED_TRUST_TEST"),
                "1",
                StringComparison.Ordinal))
            return;

        var profile = Environment.GetEnvironmentVariable("RATIOGHOST_ATTENDED_PROFILE");
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = Path.Combine(
                Path.GetTempPath(),
                "RatioGhost.AttendedCertificateTests",
                Guid.NewGuid().ToString("N"));
        }

        string? thumbprint = null;
        using var service = new WindowsCertificateAuthorityService(profile);
        try
        {
            await service.RequestTrustAsync();
            Assert.True(await service.IsTrustedAsync());
            using var metadata = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(profile, "tls", "ca.json")));
            thumbprint = metadata.RootElement.GetProperty("Thumbprint").GetString() ??
                         throw new InvalidOperationException("CA thumbprint metadata is missing.");

            using var leaf = await service.GetServerCertificateAsync("tracker.test");
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(
                chain.Build(leaf),
                string.Join(", ", chain.ChainStatus.Select(item => item.StatusInformation)));
        }
        finally
        {
            await service.RemoveTrustAsync();
        }

        Assert.False(await service.IsTrustedAsync());
        Assert.NotNull(thumbprint);
        Assert.False(StoreContains(StoreName.Root, thumbprint));
        Assert.False(StoreContains(StoreName.My, thumbprint));
    }

    private static bool StoreContains(StoreName storeName, string thumbprint)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .Count > 0;
    }

    private sealed class IsolatedCertificateStore : IWindowsCertificateStore
    {
        public X509Certificate2? PrivateCertificate { get; private set; }
        public X509Certificate2? TrustedCertificate { get; private set; }
        public bool RejectTrustedAdds { get; init; }
        public List<string> RemovedTrustedThumbprints { get; } = [];
        public List<string> RemovedPrivateThumbprints { get; } = [];

        public bool IsTrusted(string thumbprint) =>
            string.Equals(
                TrustedCertificate?.Thumbprint,
                thumbprint,
                StringComparison.OrdinalIgnoreCase);

        public X509Certificate2? FindPrivate(string thumbprint) =>
            string.Equals(
                PrivateCertificate?.Thumbprint,
                thumbprint,
                StringComparison.OrdinalIgnoreCase)
                ? Clone(PrivateCertificate!)
                : null;

        public X509Certificate2 ImportAndStorePrivate(
            byte[] pkcs12,
            string password,
            string friendlyName)
        {
            PrivateCertificate?.Dispose();
            PrivateCertificate = X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            return Clone(PrivateCertificate);
        }

        public void AddTrusted(X509Certificate2 certificate)
        {
            if (RejectTrustedAdds)
                return;
            TrustedCertificate?.Dispose();
            TrustedCertificate = X509CertificateLoader.LoadCertificate(
                certificate.Export(X509ContentType.Cert));
        }

        public void RemoveTrusted(string thumbprint)
        {
            RemovedTrustedThumbprints.Add(thumbprint);
            if (!IsTrusted(thumbprint))
                return;
            TrustedCertificate?.Dispose();
            TrustedCertificate = null;
        }

        public void RemovePrivate(string thumbprint)
        {
            RemovedPrivateThumbprints.Add(thumbprint);
            if (!string.Equals(
                    PrivateCertificate?.Thumbprint,
                    thumbprint,
                    StringComparison.OrdinalIgnoreCase))
                return;
            PrivateCertificate?.Dispose();
            PrivateCertificate = null;
        }

        public void Dispose()
        {
            // The production store outlives individual service instances. Tests
            // model that persistence so a second service can exercise recovery.
        }

        private static X509Certificate2 Clone(X509Certificate2 certificate) =>
            X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx),
                null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }
}
