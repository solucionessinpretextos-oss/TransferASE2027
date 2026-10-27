// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/SatValidator.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de validación SAT/e.firma.
// Interacción: Valida certificados y extrae identidad fiscal usada por login.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using FileXfer.Server.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public interface ISatCertificateValidator
{
    public sealed record CertInfo(
        string? Rfc,
        string? Curp,
        string? NombreCompleto,
        string? Serie,
        DateTime? NotBefore,
        DateTime? NotAfter,
        string? Emisor,
        string? Subject);

    public sealed record ValidationResult(bool Ok, string? NormalizedRfc, string? Error, bool BypassUsed)
    {
        public CertInfo? InfoCert { get; init; }
    }

    ValidationResult ValidateSignedLogin(
        string expectedRfc,
        byte[] cerBytes,
        string nonceId,
        string nonce,
        string deviceId,
        string signatureBase64);
}

public sealed class SatCertificateValidator : ISatCertificateValidator
{
    private static readonly Regex NetworkUriPattern = new(
        @"[A-Za-z][A-Za-z0-9+.-]*://[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> NetworkLocationExtensionOids = new(
        new[] { "2.5.29.31", "1.3.6.1.5.5.7.1.1" },
        StringComparer.Ordinal);
    private readonly SatOptions _opt;
    private readonly ILogger<SatCertificateValidator> _log;
    private readonly IHostEnvironment _environment;
    private readonly X509Certificate2Collection _trustedRoots = new();
    private readonly X509Certificate2Collection _intermediates = new();

    public SatCertificateValidator(
        IOptions<SatOptions> opt,
        ILogger<SatCertificateValidator> log,
        IHostEnvironment environment)
    {
        _opt = opt.Value;
        _log = log;
        _environment = environment;
        LoadCertificates(_opt.TrustedRootCertificatePaths, _trustedRoots, environment.ContentRootPath);
        LoadCertificates(_opt.IntermediateCertificatePaths, _intermediates, environment.ContentRootPath);
    }

    public ISatCertificateValidator.ValidationResult ValidateSignedLogin(
        string expectedRfc,
        byte[] cerBytes,
        string nonceId,
        string nonce,
        string deviceId,
        string signatureBase64)
    {
        try
        {
            using var cert = new X509Certificate2(cerBytes);

            if (!CheckDates(cert, out var err))
                return new(false, null, err, false);

            var info = ExtractCertInfo(cert, normalizedRfc: null);
            var certificateRfc = info.Rfc?.Trim().ToUpperInvariant();
            var norm = expectedRfc.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(certificateRfc) ||
                !string.Equals(certificateRfc, norm, StringComparison.Ordinal))
            {
                return new(false, null, "RFC no coincide con el certificado", false);
            }

            var keyUsage = cert.Extensions
                .OfType<X509KeyUsageExtension>()
                .FirstOrDefault();
            if (keyUsage != null &&
                !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            {
                return new(false, null, "El certificado no permite firma digital", false);
            }

            if (string.IsNullOrWhiteSpace(signatureBase64) || signatureBase64.Length > 16_384)
                return new(false, null, "Firma de e.firma inválida", false);

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureBase64);
            }
            catch (FormatException)
            {
                return new(false, null, "Formato de firma inválido", false);
            }

            var payload = LoginSignaturePayload.Create(nonceId, nonce, norm, deviceId);

            using var rsaPublic = cert.GetRSAPublicKey();
            if (rsaPublic == null)
                return new(false, null, "El certificado no contiene una clave pública RSA", false);
            if (rsaPublic.KeySize < 2048)
                return new(false, null, "La clave pública del certificado RSA es demasiado corta", false);

            if (!rsaPublic.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                return new(false, null, "La firma del nonce no es válida", false);
            }

            if (_environment.IsDevelopment() && _opt.AllowDevelopmentUntrustedChain)
            {
                _log.LogWarning(
                    "Validación de cadena/revocación SAT omitida exclusivamente en Development. Thumbprint={Thumbprint}",
                    cert.Thumbprint);
                return new(true, certificateRfc, null, true)
                {
                    InfoCert = info
                };
            }

            if (!ValidateNetworkLocations(cert, out var networkError))
                return new(false, null, networkError, false);

            // Primero se prueba posesión de la clave. Certificados arbitrarios que no puedan
            // firmar el reto nunca alcanzan la construcción de cadena ni resolución de emisores.
            if (!BuildTrustedChain(cert, out var chainError))
            {
                return new(false, null, chainError ?? "Cadena del certificado no confiable", false);
            }

            return new ISatCertificateValidator.ValidationResult(true, certificateRfc, null, false)
            {
                InfoCert = info
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error validando certificado y firma SAT");
            return new(false, null, "No fue posible validar la e.firma", false);
        }
    }

    private bool CheckDates(X509Certificate2 cert, out string? err)
    {
        var now = DateTimeOffset.UtcNow;
        var skew = TimeSpan.FromMinutes(_opt.AcceptClockSkewMinutes);

        if (now + skew < cert.NotBefore || now - skew > cert.NotAfter)
        {
            err = "Cert fuera de vigencia";
            return false;
        }

        err = null;
        return true;
    }

    private static ISatCertificateValidator.CertInfo ExtractCertInfo(X509Certificate2 cert, string? normalizedRfc)
    {
        string? rfc = null;
        string? curp = null;
        string? nombre = null;

        var subject = cert.Subject ?? string.Empty;
        var parts = subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (part.StartsWith("OID.2.5.4.45=", StringComparison.OrdinalIgnoreCase))
            {
                rfc = part["OID.2.5.4.45=".Length..].Trim();
            }
            else if (part.StartsWith("2.5.4.45=", StringComparison.OrdinalIgnoreCase))
            {
                rfc = part["2.5.4.45=".Length..].Trim();
            }
            else if (part.StartsWith("SERIALNUMBER=", StringComparison.OrdinalIgnoreCase))
            {
                var sn = part["SERIALNUMBER=".Length..].Trim();

                if (sn.Length >= 13)
                {
                    var possibleRfc = sn[..13];
                    rfc ??= possibleRfc;
                }

                if (sn.Length >= 18)
                {
                    var possibleCurp = sn[..18];
                    if (possibleCurp.Length == 18)
                        curp ??= possibleCurp;
                }
            }
            else if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                nombre = part["CN=".Length..].Trim();
            }
        }

        rfc ??= normalizedRfc;

        return new ISatCertificateValidator.CertInfo(
            Rfc: rfc,
            Curp: curp,
            NombreCompleto: nombre,
            Serie: cert.SerialNumber,
            NotBefore: cert.NotBefore,
            NotAfter: cert.NotAfter,
            Emisor: cert.Issuer,
            Subject: cert.Subject);
    }

    private bool BuildTrustedChain(X509Certificate2 cert, out string? error)
    {
        if (_trustedRoots.Count == 0)
        {
            error = "No hay raíces SAT configuradas para validar la e.firma";
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = _opt.RequireOcsp
            ? X509RevocationMode.Online
            : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.UrlRetrievalTimeout =
            TimeSpan.FromMilliseconds(Math.Max(1000, _opt.OnlineTimeoutMs));
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_trustedRoots);
        chain.ChainPolicy.ExtraStore.AddRange(_intermediates);

        if (chain.Build(cert))
        {
            error = null;
            return true;
        }

        var statuses = chain.ChainStatus
            .Select(status => status.Status)
            .Where(status => status != X509ChainStatusFlags.NoError)
            .ToArray();

        var onlyUnknownRevocation = statuses.Length > 0 && statuses.All(status =>
            (status & ~(X509ChainStatusFlags.RevocationStatusUnknown |
                        X509ChainStatusFlags.OfflineRevocation)) == 0);

        if (!_opt.FailOnUnknownRevocation && onlyUnknownRevocation)
        {
            _log.LogWarning(
                "La cadena SAT no pudo confirmar revocación; se permite por configuración explícita. Thumbprint={Thumbprint}",
                cert.Thumbprint);
            error = null;
            return true;
        }

        _log.LogWarning(
            "Cadena SAT no confiable. Thumbprint={Thumbprint}; Status={Status}",
            cert.Thumbprint,
            string.Join(',', statuses));

        error = "No fue posible establecer una cadena de confianza vigente para el certificado SAT";
        return false;
    }

    private bool ValidateNetworkLocations(X509Certificate2 cert, out string? error)
    {
        if (!_opt.RequireOcsp)
        {
            error = null;
            return true;
        }

        var allowedHosts = new HashSet<string>(
            (_opt.AllowedRevocationHosts ?? Array.Empty<string>())
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim().TrimEnd('.')),
            StringComparer.OrdinalIgnoreCase);
        if (allowedHosts.Count == 0)
        {
            error = "No hay hosts OCSP/CRL SAT permitidos en la configuración";
            return false;
        }

        foreach (var extension in cert.Extensions.OfType<X509Extension>())
        {
            if (extension.Oid?.Value is not { } oid || !NetworkLocationExtensionOids.Contains(oid))
                continue;

            var rawText = Encoding.ASCII.GetString(extension.RawData);
            var matches = NetworkUriPattern.Matches(rawText);
            foreach (Match match in matches)
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                    !allowedHosts.Contains(uri.IdnHost.TrimEnd('.')))
                {
                    error = "El certificado contiene una ubicación OCSP/CRL no autorizada";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static void LoadCertificates(
        IEnumerable<string>? paths,
        X509Certificate2Collection destination,
        string contentRootPath)
    {
        foreach (var configuredPath in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                continue;

            var normalizedPath = configuredPath.Trim().Trim('"');
            var fullPath = Path.GetFullPath(
                Path.IsPathRooted(normalizedPath)
                    ? normalizedPath
                    : Path.Combine(contentRootPath, normalizedPath));
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"No se encontró el certificado SAT configurado: {fullPath}");

            var certificate = new X509Certificate2(fullPath);
            if (certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"El almacén de confianza SAT solo acepta certificados públicos: {fullPath}");
            }

            var constraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();
            if (constraints is null || !constraints.CertificateAuthority)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"El certificado configurado como CA SAT no declara BasicConstraints CA=true: {fullPath}");
            }

            destination.Add(certificate);
        }
    }
}

internal static class LoginSignaturePayload
{
    public const string Version = "1";

    public static byte[] Create(
        string nonceId,
        string nonce,
        string rfc,
        string deviceId)
    {
        var canonical = string.Join(
            '\n',
            "FILEXFER-LOGIN-V1",
            nonceId.Trim().ToLowerInvariant(),
            nonce,
            rfc.Trim().ToUpperInvariant(),
            deviceId);

        return Encoding.UTF8.GetBytes(canonical);
    }
}
