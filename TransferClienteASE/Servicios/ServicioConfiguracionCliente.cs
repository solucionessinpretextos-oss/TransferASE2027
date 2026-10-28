// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioConfiguracionCliente.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Centraliza endpoints y validación TLS para todas las comunicaciones.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TransferClienteASE.Servicios
{
    public sealed class ServicioConfiguracionCliente
    {
        // 51236 pertenece a FileXferService y queda fuera del entorno de pruebas.
        // FileXferPruebas.Server usa el mismo puerto 51136 tanto en el origen
        // público como en su listener interno, para REST, gRPC, Dashboard y updater.
        private const string UrlProduccionPredeterminada = "https://187.141.128.194:51136";
        // Certificado TLS autofirmado del entorno de pruebas, válido hasta 2027-11-21.
        // Se fija por SHA-256 para que un equipo no administrado pueda autenticar al
        // servidor sin instalar una raíz local. La política conserva la validación de
        // nombre/IP y vigencia, y solo tolera errores de cadena propios de una raíz
        // autofirmada cuando la huella coincide exactamente.
        private const string HuellaServidorPruebasSha256 =
            "1FE5CCC72544F88183F88555127FB0E18E283E7E493A4B4038CB4897B4E9122D";
        private readonly IReadOnlyList<byte[]> _huellasCertificadoSha256;

        public ServicioConfiguracionCliente()
        {
            Ambiente = (Environment.GetEnvironmentVariable("TRANSFER_ENVIRONMENT") ?? "PRODUCCION")
                .Trim()
                .ToUpperInvariant();

            UrlBaseServidor = LeerEndpoint("TRANSFER_API_URL", UrlProduccionPredeterminada);
            // El listener HTTPS del servidor acepta HTTP/1.1 y HTTP/2; por ello REST y gRPC
            // comparten el mismo canal TLS y no se expone la carga documental por HTTP plano.
            DireccionGrpc = LeerEndpoint("TRANSFER_GRPC_URL", UrlBaseServidor);

            ValidarEndpoint(UrlBaseServidor, "TRANSFER_API_URL");
            ValidarEndpoint(DireccionGrpc, "TRANSFER_GRPC_URL");
            _huellasCertificadoSha256 = LeerHuellasCertificado();
        }

        public string UrlBaseServidor { get; }

        public string DireccionGrpc { get; }

        public string Ambiente { get; }

        private static string? _jwtActual;

        public HttpClientHandler CrearManejadorHttpSeguro()
        {
            return new HttpClientHandler
            {
                CheckCertificateRevocationList = true,
                ServerCertificateCustomValidationCallback = ValidarCertificadoServidor
            };
        }

        public void EstablecerJwtActual(string? jwt)
        {
            _jwtActual = jwt;
        }

        public string? ObtenerJwtActual()
        {
            return _jwtActual;
        }

        private bool ValidarCertificadoServidor(
            HttpRequestMessage _,
            X509Certificate2? certificado,
            X509Chain? cadena,
            SslPolicyErrors errores)
        {
            return PoliticaCertificadoServidor.Validar(
                certificado,
                cadena,
                errores,
                _huellasCertificadoSha256,
                DateTimeOffset.UtcNow);
        }

        private static string LeerEndpoint(string variable, string valorPredeterminado)
        {
            var valor = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrWhiteSpace(valor) ? valorPredeterminado : valor.Trim().TrimEnd('/');
        }

        private void ValidarEndpoint(string endpoint, string variable)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"La variable {variable} no contiene una URL válida.");

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return;

            var esDesarrolloLocal = !Ambiente.Equals("PRODUCCION", StringComparison.OrdinalIgnoreCase)
                                    && uri.IsLoopback
                                    && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

            if (!esDesarrolloLocal)
            {
                throw new InvalidOperationException(
                    $"{variable} debe usar HTTPS. HTTP solo se permite en loopback fuera de producción.");
            }
        }

        private static IReadOnlyList<byte[]> LeerHuellasCertificado()
        {
            var huellas = new List<byte[]>
            {
                Convert.FromHexString(HuellaServidorPruebasSha256)
            };

            var valor = Environment.GetEnvironmentVariable("TRANSFER_SERVER_CERT_SHA256");
            if (string.IsNullOrWhiteSpace(valor))
                return huellas;

            var entradas = valor.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entradas.Length is < 1 or > 3)
                throw new InvalidOperationException(
                    "TRANSFER_SERVER_CERT_SHA256 admite hasta tres huellas adicionales.");

            foreach (var entrada in entradas)
            {
                var normalizada = entrada
                    .Replace("sha256/", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(":", string.Empty, StringComparison.Ordinal)
                    .Replace(" ", string.Empty, StringComparison.Ordinal);

                try
                {
                    var bytes = Convert.FromHexString(normalizada);
                    if (bytes.Length != 32)
                        throw new FormatException();
                    if (!huellas.Any(existente => CryptographicOperations.FixedTimeEquals(existente, bytes)))
                        huellas.Add(bytes);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException(
                        "TRANSFER_SERVER_CERT_SHA256 debe contener huellas SHA-256 hexadecimales de 64 caracteres.");
                }
            }

            return huellas;
        }
    }

    internal static class PoliticaCertificadoServidor
    {
        private const X509ChainStatusFlags ErroresCadenaPermitidosConPin =
            X509ChainStatusFlags.UntrustedRoot |
            X509ChainStatusFlags.PartialChain |
            X509ChainStatusFlags.RevocationStatusUnknown |
            X509ChainStatusFlags.OfflineRevocation;

        internal static bool Validar(
            X509Certificate2? certificado,
            X509Chain? cadena,
            SslPolicyErrors errores,
            IReadOnlyList<byte[]> huellasPermitidas,
            DateTimeOffset ahoraUtc)
        {
            if (certificado is null)
                return false;

            // Ni siquiera un certificado fijado puede suplir una identidad distinta.
            if ((errores & (SslPolicyErrors.RemoteCertificateNotAvailable |
                            SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
                return false;

            var noAntesUtc = new DateTimeOffset(certificado.NotBefore.ToUniversalTime());
            var noDespuesUtc = new DateTimeOffset(certificado.NotAfter.ToUniversalTime());
            if (ahoraUtc < noAntesUtc || ahoraUtc > noDespuesUtc)
                return false;

            if (huellasPermitidas.Count == 0)
                return errores == SslPolicyErrors.None;

            var huellaPresentada = certificado.GetCertHash(HashAlgorithmName.SHA256);
            var coincide = false;
            foreach (var huellaPermitida in huellasPermitidas)
            {
                coincide |= CryptographicOperations.FixedTimeEquals(
                    huellaPresentada,
                    huellaPermitida);
            }
            if (!coincide)
                return false;

            if (errores == SslPolicyErrors.None)
                return true;

            if (errores != SslPolicyErrors.RemoteCertificateChainErrors ||
                cadena is null || cadena.ChainStatus.Length == 0)
                return false;

            foreach (var estado in cadena.ChainStatus)
            {
                if ((estado.Status & ~ErroresCadenaPermitidosConPin) != X509ChainStatusFlags.NoError)
                    return false;
            }

            return true;
        }
    }
}
