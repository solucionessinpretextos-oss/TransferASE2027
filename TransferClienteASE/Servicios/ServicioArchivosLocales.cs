// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioArchivosLocales.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio HTTP de documentos anexados.
// Interacción: Lista, descarga con verificación y elimina documentos registrados en Transfer_ArchivosLocales.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// ============================================================================
//  ServicioArchivosLocales
// ============================================================================
//  Encapsula llamadas REST relacionadas con documentos ya registrados:
//  - Ver documentos por requerimiento.
//  - Ver documentos por resultado.
//  - Descargar con reanudación y verificación SHA-256.
//  - Borrar documentos por baja lógica.
//
//  No sube archivos. La subida real ocurre por gRPC en ServicioCargaArchivos.
// ============================================================================
namespace TransferClienteASE.Servicios
{
    public sealed class EliminarArchivosLocalesRequest
    {
        public string RfcUsuario { get; set; } = string.Empty;
        public int IdRequerimiento { get; set; }
        public int IdColabora { get; set; }
        public List<int> IdsArchivos { get; set; } = new();
    }

    public sealed class EliminarArchivosLocalesResponse
    {
        public int Eliminados { get; set; }
    }

    public sealed class EliminarArchivosLocalesPorResultadoRequest
    {
        public string RfcUsuario { get; set; } = string.Empty;
        public int ResultadoId { get; set; }
        public int? ColaboraId { get; set; }
        public int? MovimientoId { get; set; }
        public List<int> IdsArchivos { get; set; } = new();
    }

    public sealed record DescargaArchivoProgreso(
        string Accion,
        long BytesRecibidos,
        long? BytesTotales,
        double Porcentaje,
        bool Reanudada);

    public sealed record DescargaArchivoResultado(
        string RutaDestino,
        long Bytes,
        string Sha256,
        bool Reanudada);

    public sealed class ServicioArchivosLocales
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly Func<string?> _tokenProvider;

        public ServicioArchivosLocales(HttpClient http, Func<string?> tokenProvider)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

            _json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        }

        private void EnsureBearerToken()
        {
            var token = _tokenProvider();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("No hay token de autenticación disponible.");

            if (_http.DefaultRequestHeaders.Authorization?.Parameter != token)
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }

        public async Task<DescargaArchivoResultado> DescargarArchivoAsync(
            int idArchivoLocal,
            string rutaDestino,
            IProgress<DescargaArchivoProgreso>? progreso = null,
            CancellationToken ct = default)
        {
            if (idArchivoLocal <= 0)
                throw new ArgumentOutOfRangeException(nameof(idArchivoLocal));
            if (string.IsNullOrWhiteSpace(rutaDestino))
                throw new ArgumentException("La ruta de destino es obligatoria.", nameof(rutaDestino));

            EnsureBearerToken();

            var destino = Path.GetFullPath(rutaDestino);
            var directorio = Path.GetDirectoryName(destino)
                ?? throw new InvalidOperationException("No se pudo determinar la carpeta de destino.");
            Directory.CreateDirectory(directorio);

            var parcial = $"{destino}.{idArchivoLocal}.filexfer.part";
            var bytesExistentes = File.Exists(parcial) ? new FileInfo(parcial).Length : 0L;
            progreso?.Report(new DescargaArchivoProgreso(
                bytesExistentes > 0 ? "Preparando reanudación" : "Solicitando archivo al servidor",
                bytesExistentes,
                null,
                0,
                bytesExistentes > 0));

            HttpResponseMessage response = await SendDownloadRequestAsync(
                idArchivoLocal,
                bytesExistentes,
                ct);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && bytesExistentes > 0)
            {
                response.Dispose();
                File.Delete(parcial);
                bytesExistentes = 0;
                response = await SendDownloadRequestAsync(idArchivoLocal, 0, ct);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw await CreateDownloadExceptionAsync(response, ct);

                var reanudada = bytesExistentes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!reanudada)
                    bytesExistentes = 0;

                if (reanudada && response.Content.Headers.ContentRange?.From != bytesExistentes)
                {
                    File.Delete(parcial);
                    throw new InvalidOperationException(
                        "El servidor respondió con un rango distinto al parcial local. Intente nuevamente.");
                }

                var total = response.Content.Headers.ContentRange?.Length;
                if (!total.HasValue && response.Content.Headers.ContentLength.HasValue)
                    total = bytesExistentes + response.Content.Headers.ContentLength.Value;

                var expectedHash = ReadSha256Header(response);
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                if (reanudada)
                {
                    progreso?.Report(new DescargaArchivoProgreso(
                        "Verificando el fragmento descargado anteriormente",
                        bytesExistentes,
                        total,
                        CalculatePercentage(bytesExistentes, total),
                        true));
                    await AppendFileToHashAsync(parcial, incrementalHash, ct);
                }

                var fileMode = reanudada ? FileMode.Append : FileMode.Create;
                long received = bytesExistentes;

                await using (var destination = new FileStream(
                                 parcial,
                                 fileMode,
                                 FileAccess.Write,
                                 FileShare.None,
                                 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                        if (read == 0)
                            break;

                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        incrementalHash.AppendData(buffer, 0, read);
                        received += read;

                        progreso?.Report(new DescargaArchivoProgreso(
                            reanudada ? "Reanudando descarga" : "Descargando archivo",
                            received,
                            total,
                            CalculatePercentage(received, total),
                            reanudada));
                    }

                    await destination.FlushAsync(ct);
                    destination.Flush(flushToDisk: true);
                }

                if (total.HasValue && received != total.Value)
                {
                    throw new IOException(
                        $"La descarga quedó incompleta: se recibieron {received} de {total.Value} bytes.");
                }

                progreso?.Report(new DescargaArchivoProgreso(
                    "Confirmando integridad SHA-256",
                    received,
                    total ?? received,
                    100,
                    reanudada));

                var actualHashBytes = incrementalHash.GetHashAndReset();
                byte[] expectedHashBytes;
                try
                {
                    expectedHashBytes = Convert.FromHexString(expectedHash);
                }
                catch (FormatException)
                {
                    File.Delete(parcial);
                    throw new InvalidOperationException("El servidor devolvió una huella SHA-256 inválida.");
                }

                if (expectedHashBytes.Length != actualHashBytes.Length ||
                    !CryptographicOperations.FixedTimeEquals(expectedHashBytes, actualHashBytes))
                {
                    File.Delete(parcial);
                    throw new InvalidDataException(
                        "La descarga no superó la verificación de integridad y fue eliminada.");
                }

                File.Move(parcial, destino, overwrite: true);
                progreso?.Report(new DescargaArchivoProgreso(
                    "Descarga verificada y guardada",
                    received,
                    received,
                    100,
                    reanudada));

                return new DescargaArchivoResultado(destino, received, expectedHash, reanudada);
            }
        }

        private async Task<HttpResponseMessage> SendDownloadRequestAsync(
            int idArchivoLocal,
            long offset,
            CancellationToken ct)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"archivos-locales/{idArchivoLocal}/download");
            if (offset > 0)
                request.Headers.Range = new RangeHeaderValue(offset, null);

            try
            {
                return await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static string ReadSha256Header(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("X-Content-SHA256", out var values))
                throw new InvalidOperationException("El servidor no proporcionó la huella de integridad del archivo.");

            var value = values.FirstOrDefault()?.Trim() ?? string.Empty;
            if (value.Length != 64 || !value.All(Uri.IsHexDigit))
                throw new InvalidOperationException("El servidor proporcionó una huella SHA-256 inválida.");

            return value.ToLowerInvariant();
        }

        private static async Task AppendFileToHashAsync(
            string path,
            IncrementalHash hash,
            CancellationToken ct)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
            }
        }

        private static double CalculatePercentage(long bytes, long? total)
        {
            var totalBytes = total.GetValueOrDefault();
            return totalBytes > 0
                ? Math.Clamp(bytes * 100d / totalBytes, 0d, 100d)
                : 0d;
        }

        private static async Task<Exception> CreateDownloadExceptionAsync(
            HttpResponseMessage response,
            CancellationToken ct)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new InvalidOperationException("La sesión expiró. Inicie sesión nuevamente.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new InvalidOperationException("No tiene autorización para descargar este archivo.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new FileNotFoundException("El archivo ya no está disponible o no pertenece al usuario.");

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("detail", out var detail) &&
                    !string.IsNullOrWhiteSpace(detail.GetString()))
                {
                    return new InvalidOperationException(detail.GetString());
                }
            }
            catch (JsonException)
            {
                // El servidor no devolvió ProblemDetails; se usa el mensaje HTTP sanitizado.
            }

            return new HttpRequestException(
                $"El servidor rechazó la descarga ({(int)response.StatusCode}).",
                null,
                response.StatusCode);
        }

        public async Task<int> EliminarArchivosLocalesAsync(
            string rfcUsuario,
            int idRequerimiento,
            int idColabora,
            IEnumerable<int> idsArchivos,
            CancellationToken ct = default)
        {
            if (idsArchivos is null)
                throw new ArgumentNullException(nameof(idsArchivos));

            var request = new EliminarArchivosLocalesRequest
            {
                RfcUsuario = rfcUsuario?.Trim().ToUpperInvariant() ?? string.Empty,
                IdRequerimiento = idRequerimiento,
                IdColabora = idColabora,
                IdsArchivos = new List<int>(idsArchivos)
            };

            if (request.IdsArchivos.Count == 0)
                return 0;

            EnsureBearerToken();

            var response = await _http.PostAsJsonAsync(
                "archivos-locales/eliminar",
                request,
                _json,
                ct);

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<EliminarArchivosLocalesResponse>(_json, ct);
            return payload?.Eliminados ?? 0;
        }

        public async Task<int> EliminarArchivosLocalesPorResultadoAsync(
            string rfcUsuario,
            int resultadoId,
            int colaboraId,
            int movimientoId,
            IEnumerable<int> idsArchivos,
            CancellationToken ct = default)
        {
            if (resultadoId <= 0)
                throw new ArgumentException("resultadoId debe ser mayor a cero.", nameof(resultadoId));
            if (colaboraId <= 0)
                throw new ArgumentException("colaboraId debe ser mayor a cero.", nameof(colaboraId));
            if (movimientoId <= 0)
                throw new ArgumentException("movimientoId debe ser mayor a cero.", nameof(movimientoId));

            if (idsArchivos is null)
                throw new ArgumentNullException(nameof(idsArchivos));

            var request = new EliminarArchivosLocalesPorResultadoRequest
            {
                RfcUsuario = rfcUsuario?.Trim().ToUpperInvariant() ?? string.Empty,
                ResultadoId = resultadoId,
                ColaboraId = colaboraId,
                MovimientoId = movimientoId,
                IdsArchivos = new List<int>(idsArchivos)
            };

            if (request.IdsArchivos.Count == 0)
                return 0;

            EnsureBearerToken();

            var response = await _http.PostAsJsonAsync(
                "archivos-locales/eliminar-por-resultado",
                request,
                _json,
                ct);

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<EliminarArchivosLocalesResponse>(_json, ct);
            return payload?.Eliminados ?? 0;
        }
    }
}
