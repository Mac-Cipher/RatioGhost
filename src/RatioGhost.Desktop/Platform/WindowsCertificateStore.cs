using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace RatioGhost.Desktop.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCertificateStore : IWindowsCertificateStore
{
    public bool IsTrusted(string thumbprint)
    {
        using var store = Open(StoreName.Root, OpenFlags.ReadOnly);
        return Find(store, thumbprint).Count > 0;
    }

    public X509Certificate2? FindPrivate(string thumbprint)
    {
        using var store = Open(StoreName.My, OpenFlags.ReadOnly);
        return Find(store, thumbprint)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificate => certificate.HasPrivateKey);
    }

    public X509Certificate2 ImportAndStorePrivate(
        byte[] pkcs12,
        string password,
        string friendlyName)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(
            pkcs12,
            password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        certificate.FriendlyName = friendlyName;
        using var store = Open(StoreName.My, OpenFlags.ReadWrite);
        store.Add(certificate);
        return certificate;
    }

    public void AddTrusted(X509Certificate2 certificate)
    {
        using var store = Open(StoreName.Root, OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public void RemoveTrusted(string thumbprint) =>
        Remove(StoreName.Root, thumbprint);

    public void RemovePrivate(string thumbprint) =>
        Remove(StoreName.My, thumbprint);

    public void Dispose()
    {
    }

    private static X509Store Open(StoreName name, OpenFlags flags)
    {
        var store = new X509Store(name, StoreLocation.CurrentUser);
        store.Open(flags);
        return store;
    }

    private static X509Certificate2Collection Find(
        X509Store store,
        string thumbprint) =>
        store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);

    private static void Remove(StoreName storeName, string thumbprint)
    {
        using var store = Open(storeName, OpenFlags.ReadWrite);
        foreach (var certificate in Find(store, thumbprint))
            store.Remove(certificate);
    }
}
