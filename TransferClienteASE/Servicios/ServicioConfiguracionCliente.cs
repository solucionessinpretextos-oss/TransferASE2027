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
        private const string UrlProduccionPredeterminada = "https://127.0.0.1:51136";
        //private const string UrlProduccionPredeterminada = "https://187.141.128.194:51236";
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
            X509Chain? __,
            SslPolicyErrors errores)
        {
            if (certificado is null)
                return false;

            // El pin complementa la PKI del sistema operativo; nunca la sustituye. Esto evita
            // aceptar un certificado fijado pero expirado, revocado o emitido para otro nombre.
            if (errores != SslPolicyErrors.None)
                return false;

            if (_huellasCertificadoSha256.Count > 0)
            {
                var huellaPresentada = certificado.GetCertHash(HashAlgorithmName.SHA256);
                var coincide = false;
                foreach (var huellaPermitida in _huellasCertificadoSha256)
                {
                    coincide |= CryptographicOperations.FixedTimeEquals(
                        huellaPresentada,
                        huellaPermitida);
                }
                return coincide;
            }

            return true;
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
            var valor = Environment.GetEnvironmentVariable("TRANSFER_SERVER_CERT_SHA256");
            if (string.IsNullOrWhiteSpace(valor))
                return Array.Empty<byte[]>();

            var entradas = valor.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entradas.Length is < 1 or > 4)
                throw new InvalidOperationException("TRANSFER_SERVER_CERT_SHA256 admite entre una y cuatro huellas.");

            var huellas = new List<byte[]>(entradas.Length);
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
}
