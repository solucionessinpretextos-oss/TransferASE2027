// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioAutenticacion.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio HTTP de autenticación del cliente.
// Interacción: Solicita nonce, firma con e.firma y obtiene JWT/refresh token del backend.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TransferClienteASE.Modelos;

namespace TransferClienteASE.Servicios
{
    public sealed class ServicioAutenticacion
    {
        private readonly ServicioConfiguracionCliente _configuracion;
        private readonly HttpClient _http;

        public ServicioAutenticacion(ServicioConfiguracionCliente configuracion)
        {
            _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));

            var handler = configuracion.CrearManejadorHttpSeguro();

            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuracion.UrlBaseServidor, UriKind.Absolute),
                Timeout = TimeSpan.FromMinutes(2)
            };
        }

        public sealed record CertificadoSesion(
            string? Rfc,
            string? Curp,
            string? NombreCompleto,
            string? Serie,
            DateTimeOffset? NotBefore,
            DateTimeOffset? NotAfter,
            string? Emisor,
            string? Subject);

        public sealed record ResultadoInicioSesion(
            bool EsCorrecto,
            string? RfcNormalizado,
            string? TokenJwt,
            string? MensajeError,
            string? RefreshToken,
            DateTimeOffset? RefreshExpiraUtc,
            CertificadoSesion? Certificado);

        public async Task<ResultadoInicioSesion> IniciarSesionAsync(
            string rutaCer,
            string rutaKey,
            string contrasena,
            CancellationToken ct)
        {
            if (!File.Exists(rutaCer))
                return new ResultadoInicioSesion(false, null, null, "No se encontró el archivo CER.", null, null, null);
            if (!File.Exists(rutaKey))
                return new ResultadoInicioSesion(false, null, null, "No se encontró el archivo KEY.", null, null, null);

            var cerBytes = await File.ReadAllBytesAsync(rutaCer, ct);

            var rfcCertificado = ExtraerRfcDesdeCertificado(cerBytes);
            if (string.IsNullOrWhiteSpace(rfcCertificado))
                return new ResultadoInicioSesion(false, null, null,
                    "No se pudo extraer el RFC del certificado (OID 2.5.4.45).", null, null, null);

            HttpResponseMessage resNonce;
            try
            {
                resNonce = await _http.PostAsync("auth/nonce", null, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                return new ResultadoInicioSesion(false, null, null,
                    "No se pudo establecer una conexión segura con el servidor. " +
                    "Verifique que está conectado a la red de la ASE e intente de nuevo.",
                    null,
                    null,
                    null);
            }

            if (!resNonce.IsSuccessStatusCode)
            {
                var msg = $"Error al obtener nonce (HTTP {(int)resNonce.StatusCode}).";
                return new ResultadoInicioSesion(false, null, null, msg, null, null, null);
            }

            var jsonNonce = await resNonce.Content.ReadAsStringAsync(ct);
            using var docNonce = JsonDocument.Parse(jsonNonce);
            var rootNonce = docNonce.RootElement;

            var nonceId = rootNonce.GetProperty("nonceId").GetString() ?? string.Empty;
            var nonce = rootNonce.GetProperty("nonce").GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nonceId) || string.IsNullOrWhiteSpace(nonce))
            {
                return new ResultadoInicioSesion(false, null, null,
                    "Respuesta inválida al solicitar nonce.", null, null, null);
            }

            var keyBytes = await File.ReadAllBytesAsync(rutaKey, ct);

            var deviceId = ObtenerIdDispositivo();
            SesionAplicacion.DeviceId = deviceId;

            string signatureBase64;
            try
            {
                signatureBase64 = FirmarNonce(
                    keyBytes,
                    contrasena,
                    nonceId,
                    nonce,
                    rfcCertificado!,
                    deviceId,
                    cerBytes);
            }
            catch (CryptographicException)
            {
                return new ResultadoInicioSesion(
                    false,
                    null,
                    null,
                    "La contraseña de la e.firma es incorrecta o la llave KEY no corresponde al certificado CER.",
                    null,
                    null,
                    null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }

            var solicitud = new SolicitudLogin
            {
                rfc = rfcCertificado!,
                cerBase64 = Convert.ToBase64String(cerBytes),
                nonceId = nonceId,
                nonce = nonce,
                signatureVersion = "1",
                signatureBase64 = signatureBase64,
                deviceId = deviceId
            };

            HttpResponseMessage resLogin;
            string jsonLogin;
            try
            {
                resLogin = await _http.PostAsJsonAsync("auth/login-signed", solicitud, ct);
                jsonLogin = await resLogin.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                return new ResultadoInicioSesion(false, null, null,
                    "No se pudo establecer una conexión segura con el servidor durante el inicio de sesión.",
                    null,
                    null,
                    null);
            }

            if (!resLogin.IsSuccessStatusCode)
            {
                var msgProblem = ObtenerMensajeProblemDetails(jsonLogin);
                var msg = msgProblem ?? $"Error al iniciar sesión (HTTP {(int)resLogin.StatusCode}).";
                return new ResultadoInicioSesion(false, null, null, msg, null, null, null);
            }

            using var docLogin = JsonDocument.Parse(jsonLogin);
            var rootLogin = docLogin.RootElement;

            var token = rootLogin.TryGetProperty("token", out var tProp) ? tProp.GetString() : null;
            var rfcNorm = rootLogin.TryGetProperty("rfc", out var rProp) ? rProp.GetString() : null;
            var refreshToken = rootLogin.TryGetProperty("refreshToken", out var rtProp) ? rtProp.GetString() : null;

            DateTimeOffset? refreshExpiraUtc = null;
            if (rootLogin.TryGetProperty("refreshExpiresUtc", out var expProp) &&
                expProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expProp.GetString(), out var parsed))
            {
                refreshExpiraUtc = parsed;
            }

            CertificadoSesion? certificadoSesion = null;
            if (rootLogin.TryGetProperty("certificado", out var certProp) &&
                certProp.ValueKind == JsonValueKind.Object)
            {
                string? certRfc = certProp.TryGetProperty("rfc", out var cr) && cr.ValueKind == JsonValueKind.String
                    ? cr.GetString()
                    : null;

                string? certCurp = certProp.TryGetProperty("curp", out var cc) && cc.ValueKind == JsonValueKind.String
                    ? cc.GetString()
                    : null;

                string? certNombre = certProp.TryGetProperty("nombreCompleto", out var cn) && cn.ValueKind == JsonValueKind.String
                    ? cn.GetString()
                    : null;

                string? certSerie = certProp.TryGetProperty("serie", out var cs) && cs.ValueKind == JsonValueKind.String
                    ? cs.GetString()
                    : null;

                DateTimeOffset? certNotBefore = null;
                if (certProp.TryGetProperty("notBefore", out var cnb) &&
                    cnb.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(cnb.GetString(), out var nbParsed))
                {
                    certNotBefore = nbParsed;
                }

                DateTimeOffset? certNotAfter = null;
                if (certProp.TryGetProperty("notAfter", out var cna) &&
                    cna.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(cna.GetString(), out var naParsed))
                {
                    certNotAfter = naParsed;
                }

                string? certEmisor = certProp.TryGetProperty("emisor", out var ce) && ce.ValueKind == JsonValueKind.String
                    ? ce.GetString()
                    : null;

                string? certSubject = certProp.TryGetProperty("subject", out var csu) && csu.ValueKind == JsonValueKind.String
                    ? csu.GetString()
                    : null;

                certificadoSesion = new CertificadoSesion(
                    certRfc,
                    certCurp,
                    certNombre,
                    certSerie,
                    certNotBefore,
                    certNotAfter,
                    certEmisor,
                    certSubject);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                var msg = rootLogin.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "No se pudo iniciar sesión."
                    : "No se pudo iniciar sesión.";
                return new ResultadoInicioSesion(false, null, null, msg, null, null, null);
            }

            SesionAplicacion.Rfc = (rfcNorm ?? rfcCertificado).ToUpperInvariant();
            SesionAplicacion.TokenJwt = token;
            SesionAplicacion.RefreshToken = refreshToken ?? string.Empty;
            SesionAplicacion.RefreshTokenExpiraUtc = refreshExpiraUtc;

            return new ResultadoInicioSesion(
                EsCorrecto: true,
                RfcNormalizado: SesionAplicacion.Rfc,
                TokenJwt: token,
                MensajeError: null,
                RefreshToken: refreshToken,
                RefreshExpiraUtc: refreshExpiraUtc,
                Certificado: certificadoSesion);
        }

        private sealed class SolicitudLogin
        {
            public string rfc { get; set; } = string.Empty;
            public string cerBase64 { get; set; } = string.Empty;
            public string nonceId { get; set; } = string.Empty;
            public string nonce { get; set; } = string.Empty;
            public string signatureVersion { get; set; } = "1";
            public string signatureBase64 { get; set; } = string.Empty;
            public string deviceId { get; set; } = string.Empty;
        }

        private static string FirmarNonce(
            byte[] keyBytes,
            string contrasena,
            string nonceId,
            string nonce,
            string rfc,
            string deviceId,
            byte[] cerBytes)
        {
            using var rsaPrivada = RSA.Create();
            rsaPrivada.ImportEncryptedPkcs8PrivateKey(
                contrasena.AsSpan(),
                keyBytes,
                out var bytesLeidos);

            if (bytesLeidos != keyBytes.Length)
                throw new CryptographicException("La llave KEY contiene datos adicionales no válidos.");

            var payload = CrearPayloadFirma(nonceId, nonce, rfc, deviceId);
            var firma = rsaPrivada.SignData(
                payload,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            using var certificado = new X509Certificate2(cerBytes);
            using var rsaPublica = certificado.GetRSAPublicKey();
            if (rsaPublica == null ||
                !rsaPublica.VerifyData(payload, firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw new CryptographicException("La llave KEY no corresponde al certificado CER.");
            }

            return Convert.ToBase64String(firma);
        }

        internal static byte[] CrearPayloadFirma(
            string nonceId,
            string nonce,
            string rfc,
            string deviceId)
        {
            var canonico = string.Join(
                '\n',
                "FILEXFER-LOGIN-V1",
                nonceId.Trim().ToLowerInvariant(),
                nonce,
                rfc.Trim().ToUpperInvariant(),
                deviceId);

            return Encoding.UTF8.GetBytes(canonico);
        }

        private static string ObtenerIdDispositivo()
        {
            if (!string.IsNullOrWhiteSpace(SesionAplicacion.DeviceId))
                return SesionAplicacion.DeviceId;

            return ServicioIdentidadDispositivo.ObtenerOCrear();
        }

        private static string? ExtraerRfcDesdeCertificado(byte[] cerBytes)
        {
            var cert = new X509Certificate2(cerBytes);
            var subject = cert.Subject ?? string.Empty;

            const string prefijo = "OID.2.5.4.45=";
            var idx = subject.IndexOf(prefijo, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var resto = subject[(idx + prefijo.Length)..];
                var fin = resto.IndexOf(',');
                var valor = fin >= 0 ? resto[..fin] : resto;
                return valor.Trim().ToUpperInvariant();
            }

            return null;
        }

        private static string? ObtenerMensajeProblemDetails(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("title", out var t)) return t.GetString();
                if (root.TryGetProperty("detail", out var d)) return d.GetString();
                if (root.TryGetProperty("error", out var e)) return e.GetString();
            }
            catch
            {
            }

            return null;
        }
    }
}
