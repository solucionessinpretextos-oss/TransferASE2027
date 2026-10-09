// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioArchivosLocales.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio HTTP de documentos anexados.
// Interacción: Lista y elimina documentos registrados en Transfer_ArchivosLocales.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


// ============================================================================
//  ServicioArchivosLocales
// ============================================================================
//  Encapsula llamadas REST relacionadas con documentos ya registrados:
//  - Ver documentos por requerimiento.
//  - Ver documentos por resultado.
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
