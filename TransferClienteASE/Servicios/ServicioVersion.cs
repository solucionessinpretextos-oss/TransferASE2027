// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioVersion.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio del cliente: encapsula HTTP REST, gRPC, e.firma, configuración, actualización o utilidades de negocio.
// Interacción: Los ViewModels llaman estos servicios; aquí se traduce la UI a llamadas hacia FileXfer.Server.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransferClienteASE.Servicios
{
    public sealed class ServicioVersion
    {
        private readonly ServicioConfiguracionCliente _configuracion;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private const string ClientId = "transfer-cliente-ase";

        private const string MensajeSoporte =
            "Si el problema persiste, contacte a Soporte ASE (juan.carrillo@ase-sinaloa.gob.mx, ext. 229).";

        public ServicioVersion(ServicioConfiguracionCliente configuracion)
        {
            _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
        }

        public sealed record ResultadoVersion(
            bool HayNuevaVersion,
            bool EsActualizacionObligatoria,
            string? MensajeError,
            string VersionActual,
            string? VersionDisponible,
            string? UrlDescarga,
            string? NotasVersion,
            string? Sha256 = null,
            string? SignatureBase64 = null,
            string? SignatureAlgorithm = null);

        public async Task<ResultadoVersion> RevisarVersionAsync(CancellationToken ct)
        {
            var versionActual = ObtenerVersionActual();

            var handler = _configuracion.CrearManejadorHttpSeguro();

            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(_configuracion.UrlBaseServidor, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(30)
            };

            var requestUri = $"admin/client-updates/{ClientId}?currentVersion={Uri.EscapeDataString(versionActual)}";

            try
            {
                using var response = await http.GetAsync(requestUri, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new ResultadoVersion(
                        HayNuevaVersion: false,
                        EsActualizacionObligatoria: false,
                        MensajeError: null,
                        VersionActual: versionActual,
                        VersionDisponible: null,
                        UrlDescarga: null,
                        NotasVersion: null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var msg = "No se pudo verificar si hay actualizaciones disponibles "
                              + $"(código {(int)response.StatusCode}). "
                              + "Puede continuar usando el sistema normalmente. "
                              + MensajeSoporte;

                    return new ResultadoVersion(
                        HayNuevaVersion: false,
                        EsActualizacionObligatoria: false,
                        MensajeError: msg,
                        VersionActual: versionActual,
                        VersionDisponible: null,
                        UrlDescarga: null,
                        NotasVersion: null);
                }

                var info = await response.Content.ReadFromJsonAsync<ClientUpdateInfoFromServer>(JsonOptions, ct);
                if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
                {
                    return new ResultadoVersion(
                        HayNuevaVersion: false,
                        EsActualizacionObligatoria: false,
                        MensajeError: null,
                        VersionActual: versionActual,
                        VersionDisponible: null,
                        UrlDescarga: null,
                        NotasVersion: null);
                }

                if (!Version.TryParse(versionActual, out var vActual))
                {
                    vActual = new Version(1, 0, 0, 0);
                }

                if (!Version.TryParse(info.LatestVersion, out var vServidor))
                {
                    var msg = "El servidor devolvió un formato de versión no válido "
                              + $"(\"{info.LatestVersion}\"). "
                              + MensajeSoporte;

                    return new ResultadoVersion(
                        HayNuevaVersion: false,
                        EsActualizacionObligatoria: false,
                        MensajeError: msg,
                        VersionActual: versionActual,
                        VersionDisponible: info.LatestVersion,
                        UrlDescarga: info.DownloadUrl,
                        NotasVersion: info.ReleaseNotes);
                }

                var hayNueva = vServidor > vActual;

                Version? vMinima = null;
                if (!string.IsNullOrWhiteSpace(info.MinSupportedVersion) &&
                    Version.TryParse(info.MinSupportedVersion, out var tmpMin))
                {
                    vMinima = tmpMin;
                }

                var esObligatoria = hayNueva && vMinima is not null && vActual < vMinima;
                if (info.IsCritical)
                {
                    esObligatoria = true;
                }

                return new ResultadoVersion(
                    HayNuevaVersion: hayNueva,
                    EsActualizacionObligatoria: esObligatoria,
                    MensajeError: null,
                    VersionActual: versionActual,
                    VersionDisponible: info.LatestVersion,
                    UrlDescarga: info.DownloadUrl,
                    NotasVersion: info.ReleaseNotes,
                    Sha256: info.Sha256,
                    SignatureBase64: info.SignatureBase64,
                    SignatureAlgorithm: info.SignatureAlgorithm);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var msg = "No se pudo conectar con el servidor de actualizaciones. "
                          + "Verifique que tenga acceso a la red de la ASE. "
                          + MensajeSoporte
                          + Environment.NewLine
                          + "Detalle técnico: " + ex.Message;

                return new ResultadoVersion(
                    HayNuevaVersion: false,
                    EsActualizacionObligatoria: false,
                    MensajeError: msg,
                    VersionActual: versionActual,
                    VersionDisponible: null,
                    UrlDescarga: null,
                    NotasVersion: null);
            }
        }

        public async Task<(bool Exito, string? MensajeError)> DescargarEInstalarAsync(
            ResultadoVersion update,
            CancellationToken ct)
        {
            const long maxPackageBytes = 512L * 1024L * 1024L;
            string? rutaDestino = null;

            if (update is null || string.IsNullOrWhiteSpace(update.UrlDescarga))
            {
                return (false, "El servidor indicó una actualización, pero no proporcionó una ruta de descarga. "
                               + MensajeSoporte);
            }

            if (string.IsNullOrWhiteSpace(update.VersionDisponible) ||
                string.IsNullOrWhiteSpace(update.Sha256) ||
                update.Sha256.Length != 64 ||
                string.IsNullOrWhiteSpace(update.SignatureBase64) ||
                !string.Equals(update.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.Ordinal))
            {
                return (false, "La actualización no contiene un manifiesto criptográfico completo y fue rechazada. "
                               + MensajeSoporte);
            }

            try
            {
                var uri = new Uri(update.UrlDescarga, UriKind.Absolute);
                if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "La actualización fue rechazada porque la descarga no usa HTTPS. "
                                   + MensajeSoporte);
                }

                byte[] expectedHash;
                byte[] signature;
                try
                {
                    expectedHash = Convert.FromHexString(update.Sha256);
                    signature = Convert.FromBase64String(update.SignatureBase64);
                }
                catch (FormatException)
                {
                    return (false, "El hash o la firma de la actualización tiene un formato inválido. " + MensajeSoporte);
                }

                var publicKeyPath = Environment.GetEnvironmentVariable("TRANSFER_UPDATE_PUBLIC_KEY_PATH");
                if (string.IsNullOrWhiteSpace(publicKeyPath) || !File.Exists(publicKeyPath))
                {
                    return (false, "No está configurada la clave pública para verificar actualizaciones. "
                                   + "Por seguridad no se ejecutará el paquete. " + MensajeSoporte);
                }

                using var verifier = RSA.Create();
                verifier.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath, ct));
                if (verifier.KeySize < 2048)
                {
                    return (false, "La clave pública de actualizaciones es demasiado corta. " +
                                   "Por seguridad no se ejecutará el paquete. " + MensajeSoporte);
                }

                var handler = _configuracion.CrearManejadorHttpSeguro();
                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };

                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.RequestMessage?.RequestUri is not { Scheme: var finalScheme } ||
                    !finalScheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "La descarga fue redirigida fuera de HTTPS y se rechazó. " + MensajeSoporte);
                }
                if (!response.IsSuccessStatusCode)
                {
                    return (false, "No se pudo descargar la actualización "
                                   + $"(código {(int)response.StatusCode}). "
                                   + "Intente de nuevo más tarde. "
                                   + MensajeSoporte);
                }

                if (response.Content.Headers.ContentLength is long declaredLength &&
                    declaredLength > maxPackageBytes)
                {
                    return (false, "El paquete de actualización excede el tamaño permitido y fue rechazado. "
                                   + MensajeSoporte);
                }

                var nombreArchivo = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(nombreArchivo))
                    nombreArchivo = "TransferClienteASE-setup.exe";

                var extension = Path.GetExtension(nombreArchivo);
                if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "El paquete de actualización no es un instalador EXE o MSI permitido. "
                                   + MensajeSoporte);
                }

                var carpeta = Path.Combine(
                    Path.GetTempPath(),
                    "TransferClienteASE",
                    "updates",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(carpeta);
                rutaDestino = Path.Combine(carpeta, nombreArchivo);

                await using (var origen = await response.Content.ReadAsStreamAsync(ct))
                await using (var destino = new FileStream(
                                 rutaDestino,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var read = await origen.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                        if (read <= 0)
                            break;

                        if (total > maxPackageBytes - read)
                            throw new InvalidDataException("El paquete excede el tamaño máximo permitido.");

                        await destino.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                    }

                    await destino.FlushAsync(ct);
                }

                string? verificationFailure = null;
                await using (var verifiedPackage = new FileStream(
                                 rutaDestino,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    // El mismo handle permanece abierto desde el hash hasta Process.Start.
                    // FileShare.Read permite al cargador leer, pero impide sustituir o borrar
                    // el archivo verificado durante la ventana crítica.
                    var actualHash = await SHA256.HashDataAsync(verifiedPackage, ct);
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    {
                        verificationFailure = "El paquete descargado no coincide con el SHA-256 publicado. No se ejecutará. ";
                    }
                    else
                    {
                        var canonical = string.Join("\n", new[]
                        {
                            "FILEXFER-UPDATE-V1",
                            ClientId,
                            update.VersionDisponible,
                            update.Sha256.ToLowerInvariant()
                        });
                        var signatureOk = verifier.VerifyData(
                            Encoding.UTF8.GetBytes(canonical),
                            signature,
                            HashAlgorithmName.SHA256,
                            RSASignaturePadding.Pss);

                        if (!signatureOk)
                        {
                            verificationFailure = "La firma criptográfica de la actualización no es válida. No se ejecutará. ";
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = rutaDestino,
                                UseShellExecute = true
                            });
                        }
                    }
                }

                if (verificationFailure is not null)
                {
                    File.Delete(rutaDestino);
                    return (false, verificationFailure + MensajeSoporte);
                }

                return (true, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(rutaDestino) && File.Exists(rutaDestino))
                        File.Delete(rutaDestino);
                }
                catch
                {
                }

                var msg = "Ocurrió un error al descargar, verificar o iniciar la actualización. "
                          + "Intente de nuevo más tarde. "
                          + MensajeSoporte
                          + Environment.NewLine
                          + "Detalle técnico: " + ex.Message;

                return (false, msg);
            }
        }

        private static string ObtenerVersionActual()
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version;
            return version?.ToString() ?? "1.0.0.0";
        }

        private sealed class ClientUpdateInfoFromServer
        {
            public string ClientId { get; set; } = string.Empty;
            public string LatestVersion { get; set; } = string.Empty;
            public string? MinSupportedVersion { get; set; }
            public string DownloadUrl { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public string SignatureBase64 { get; set; } = string.Empty;
            public string SignatureAlgorithm { get; set; } = string.Empty;
            public string? ReleaseNotes { get; set; }
            public bool IsCritical { get; set; }
        }
    }
}
