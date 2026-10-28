using System.Net.Security;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TransferClienteASE.Servicios;

using var certificado = CrearCertificado(
    DateTimeOffset.UtcNow.AddDays(-1),
    DateTimeOffset.UtcNow.AddDays(30));
using var cadena = CrearCadenaNoConfiable(certificado);

var huellaCorrecta = new[] { certificado.GetCertHash(HashAlgorithmName.SHA256) };
var huellaIncorrecta = new[] { RandomNumberGenerator.GetBytes(32) };

Require(
    PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.RemoteCertificateChainErrors,
        huellaCorrecta,
        DateTimeOffset.UtcNow),
    "El certificado autofirmado fijado fue rechazado.");

Require(
    !PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.RemoteCertificateChainErrors,
        huellaIncorrecta,
        DateTimeOffset.UtcNow),
    "Se aceptó un certificado con huella distinta.");

Require(
    !PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.RemoteCertificateNameMismatch |
        SslPolicyErrors.RemoteCertificateChainErrors,
        huellaCorrecta,
        DateTimeOffset.UtcNow),
    "Se aceptó un certificado emitido para otro nombre.");

Require(
    !PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.RemoteCertificateChainErrors,
        huellaCorrecta,
        certificado.NotAfter.ToUniversalTime().AddSeconds(1)),
    "Se aceptó un certificado vencido.");

Require(
    PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.None,
        Array.Empty<byte[]>(),
        DateTimeOffset.UtcNow),
    "La PKI normal rechazó un certificado sin errores.");

Require(
    !PoliticaCertificadoServidor.Validar(
        certificado,
        cadena,
        SslPolicyErrors.RemoteCertificateChainErrors,
        Array.Empty<byte[]>(),
        DateTimeOffset.UtcNow),
    "La PKI normal aceptó una cadena no confiable sin pin.");

var updateTestRoot = Path.Combine(
    Path.GetTempPath(),
    "TransferClienteASE.Tls.SmokeTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(updateTestRoot);
try
{
    var installedExecutable = Path.Combine(
        updateTestRoot,
        "TransferClienteASE-2.2.1.5-standalone-win-x64.exe");
    File.WriteAllText(installedExecutable, "synthetic-client");

    var updateDestination = ServicioVersion.ResolverDestinoActualizacion(installedExecutable);
    Require(
        string.Equals(
            updateDestination.DirectorioInstalacion,
            Path.GetFullPath(updateTestRoot),
            StringComparison.OrdinalIgnoreCase),
        "La actualización no conservó el directorio del ejecutable que inició el usuario.");
    Require(
        updateDestination.EjecutableInstalado == Path.GetFileName(installedExecutable),
        "La actualización no conservó el nombre exacto del ejecutable instalado.");
    Require(
        updateDestination.EjecutableEnPaquete == "TransferClienteASE.exe",
        "La actualización no distinguió el EXE canónico del paquete.");
}
finally
{
    Directory.Delete(updateTestRoot, recursive: true);
}

Console.WriteLine(
    "PASS tls-server-certificate=pinned-self-signed wrong-pin-name-expiry-and-unpinned-chain-rejected update-target=process-path");
return 0;

static X509Certificate2 CrearCertificado(DateTimeOffset noAntes, DateTimeOffset noDespues)
{
    using var rsa = RSA.Create(2048);
    var solicitud = new CertificateRequest(
        "CN=transfer-pruebas.ase-sinaloa.local",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddIpAddress(IPAddress.Parse("187.141.128.194"));
    solicitud.CertificateExtensions.Add(san.Build());
    solicitud.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));
    return solicitud.CreateSelfSigned(noAntes, noDespues);
}

static X509Chain CrearCadenaNoConfiable(X509Certificate2 certificado)
{
    var cadena = new X509Chain();
    cadena.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    _ = cadena.Build(certificado);
    return cadena;
}

static void Require(bool condicion, string mensaje)
{
    if (!condicion)
        throw new InvalidOperationException(mensaje);
}
