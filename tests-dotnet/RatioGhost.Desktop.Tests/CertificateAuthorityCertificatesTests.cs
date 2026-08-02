using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RatioGhost.Desktop.Platform;

namespace RatioGhost.Desktop.Tests;

public sealed class CertificateAuthorityCertificatesTests
{
    [Fact]
    public void DnsLeaf_BuildsUnderTheIssuedRootWithServerAuthentication()
    {
        using var root = CertificateAuthorityCertificates.CreateRoot("portable-test");
        using var leaf = CertificateAuthorityCertificates.CreateServerCertificate(root, "tracker.test");

        Assert.True(root.HasPrivateKey);
        Assert.True(leaf.HasPrivateKey);
        Assert.Equal("tracker.test", leaf.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        var enhancedKeyUsage = Assert.Single(
            leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Contains(
            enhancedKeyUsage.EnhancedKeyUsages.Cast<Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        AssertValidChain(leaf, root);
    }

    [Fact]
    public void IpLeaf_UsesTheIpSubjectAlternativeName()
    {
        using var root = CertificateAuthorityCertificates.CreateRoot("portable-ip-test");
        using var leaf = CertificateAuthorityCertificates.CreateServerCertificate(root, "127.0.0.1");

        Assert.Contains(
            leaf.Extensions,
            extension => extension.Oid?.Value == "2.5.29.17");
        AssertValidChain(leaf, root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a host")]
    [InlineData("tracker/announce")]
    public void InvalidHost_IsRejectedBeforeIssuance(string host)
    {
        using var root = CertificateAuthorityCertificates.CreateRoot("portable-invalid-test");

        Assert.Throws<ArgumentException>(
            () => CertificateAuthorityCertificates.CreateServerCertificate(root, host));
    }

    private static void AssertValidChain(
        X509Certificate2 leaf,
        X509Certificate2 root)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(
            chain.Build(leaf),
            string.Join(", ", chain.ChainStatus.Select(item => item.StatusInformation)));
    }
}
