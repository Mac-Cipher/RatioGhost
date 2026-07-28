param(
    [Parameter(Mandatory = $true)][string]$CertificatePath,
    [Parameter(Mandatory = $true)][string]$KeyPath
)

$ErrorActionPreference = 'Stop'

function Convert-ToPem([string]$Label, [byte[]]$Bytes) {
    $base64 = [Convert]::ToBase64String($Bytes, [Base64FormattingOptions]::InsertLineBreaks)
    return "-----BEGIN $Label-----`r`n$base64`r`n-----END $Label-----`r`n"
}

$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Ratio Ghost Local Proxy',
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1
    )
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true)
    )
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true
        )
    )
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset]::UtcNow.AddYears(5)
    )
    [IO.File]::WriteAllText($CertificatePath, (Convert-ToPem 'CERTIFICATE' $certificate.Export('Cert')))
    if ($null -ne $rsa.PSObject.Methods['ExportPkcs8PrivateKey']) {
        $privateKey = $rsa.ExportPkcs8PrivateKey()
    } elseif ($rsa -is [Security.Cryptography.RSACng]) {
        $privateKey = $rsa.Key.Export([Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    } else {
        throw 'This Windows installation cannot export a PKCS#8 private key.'
    }
    [IO.File]::WriteAllText($KeyPath, (Convert-ToPem 'PRIVATE KEY' $privateKey))
} finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    $rsa.Dispose()
}
