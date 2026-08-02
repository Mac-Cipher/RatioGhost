using System.Security.Cryptography.X509Certificates;

namespace RatioGhost.Core.Platform;

public sealed record PlatformCapability(bool IsSupported, string Description);

public interface IAutostartService
{
    PlatformCapability Capability { get; }
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface ICertificateAuthorityService
{
    PlatformCapability Capability { get; }
    Task<bool> IsTrustedAsync(CancellationToken cancellationToken = default);
    Task RequestTrustAsync(CancellationToken cancellationToken = default);
    Task RemoveTrustAsync(CancellationToken cancellationToken = default);
    Task<X509Certificate2> GetServerCertificateAsync(
        string host,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-neutral persistence/trust boundary for an installation CA.
/// Concrete adapters belong to the Desktop platform layer; the Core and Proxy
/// assemblies do not depend on Windows certificate APIs.
/// </summary>
public interface ICertificateTrustStore : IDisposable
{
    bool IsTrusted(string thumbprint);

    X509Certificate2? FindPrivate(string thumbprint);

    X509Certificate2 ImportAndStorePrivate(
        byte[] pkcs12,
        string password,
        string friendlyName);

    void AddTrusted(X509Certificate2 certificate);

    void RemoveTrusted(string thumbprint);

    void RemovePrivate(string thumbprint);
}
