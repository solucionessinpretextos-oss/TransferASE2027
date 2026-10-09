// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioCatalogosAuditoria.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio HTTP de catálogos BuDi.
// Interacción: Consume endpoints /combos para poblar la UI de selección.
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TransferClienteASE.Modelos;


// ============================================================================
//  ServicioCatalogosAuditoria
// ============================================================================
//  Cliente HTTP usado por la pantalla principal para consultar catálogos del servidor.
//  Traduce acciones del usuario a endpoints REST:
//      ObtenerAniosAsync                       -> GET /combos/anios
//      ObtenerEntesAsync                       -> GET /combos/entes
//      ObtenerActosAsync                       -> GET /combos/actos
//      ObtenerOficiosAsync                     -> GET /combos/oficios
//      ObtenerRequerimientosAsync              -> GET /combos/requerimientos
//      ObtenerResultadosCedulaAsync            -> GET /combos/resultados-cedulas
//      ObtenerResultadosInformeIndividualAsync -> GET /combos/resultados-informe-individual
//
//  Todas las llamadas protegidas requieren JWT. EnsureAuthHeader agrega el encabezado
//  Authorization: Bearer <token> antes de llamar al servidor.
// ============================================================================
namespace TransferClienteASE.Servicios
{
    public sealed class ServicioCatalogosAuditoria
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly Func<string?> _tokenProvider;

        public ServicioCatalogosAuditoria(
            HttpClient http,
            string baseAddress,
            Func<string?> tokenProvider)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

            if (string.IsNullOrWhiteSpace(baseAddress))
                throw new ArgumentException("BaseAddress no puede ser nulo o vacío.", nameof(baseAddress));

            if (!baseAddress.EndsWith("/", StringComparison.Ordinal))
                baseAddress += "/";

            _http.BaseAddress = new Uri(baseAddress, UriKind.Absolute);

            _json = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        /// <summary>
        /// Garantiza que cada petición al API lleve el token JWT vigente.
        ///
        /// Si este encabezado no viaja, el backend responderá 401 Unauthorized aunque el endpoint
        /// y los parámetros sean correctos. Este fue un punto importante al probar desde Swagger:
        /// Swagger sin Authorization no equivale al cliente WPF autenticado.
        /// </summary>
        private void EnsureAuthHeader()
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

        private static Exception WrapHttpException(string contexto, HttpResponseMessage rsp, string body)
        {
            var status = (int)rsp.StatusCode;
            var msg = $"Error HTTP {status} al {contexto}.";
            if (!string.IsNullOrWhiteSpace(body))
                msg += Environment.NewLine + body;

            return new Exception(msg);
        }

        private async Task<bool> IntentarRefreshAsync(CancellationToken ct)
        {
            await Task.CompletedTask;
            return false;
        }

        public async Task<List<AnioDto>> ObtenerAniosAsync(int cuantos = 11)
        {
            try
            {
                EnsureAuthHeader();

                var url = $"combos/anios?cuantos={cuantos}";
                var rsp = await _http.GetAsync(url);

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync();
                    throw WrapHttpException("obtener años", rsp, bodyErr);
                }

                var model = await rsp.Content.ReadFromJsonAsync<List<AnioDto>>(_json)
                            ?? new List<AnioDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener años. Verifica conectividad con el servidor.");
            }
        }

        public async Task<List<EnteDto>> ObtenerEntesAsync(string rfc, int periodo, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rfc))
                throw new ArgumentException("RFC no puede ser vacío.", nameof(rfc));

            try
            {
                EnsureAuthHeader();

                var q = HttpUtility.ParseQueryString(string.Empty);
                q["rfc"] = rfc;
                q["periodo"] = periodo.ToString();

                var url = $"combos/entes?{q}";

                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                var body = await rsp.Content.ReadAsStringAsync(ct);

                if (!rsp.IsSuccessStatusCode)
                    throw WrapHttpException("obtener entes", rsp, body);

                var model = JsonSerializer.Deserialize<List<EnteDto>>(body, _json)
                            ?? new List<EnteDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener entes. Verifica conectividad con el servidor.");
            }
        }

        public async Task<List<ActoDto>> ObtenerActosAsync(CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();

                var url = "combos/actos";
                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                var body = await rsp.Content.ReadAsStringAsync(ct);

                if (!rsp.IsSuccessStatusCode)
                    throw WrapHttpException("obtener actos", rsp, body);

                var model = JsonSerializer.Deserialize<List<ActoDto>>(body, _json)
                            ?? new List<ActoDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener actos. Verifica conectividad con el servidor.");
            }
        }

        /// <summary>
        /// Obtiene oficios para la combinación auditoría + acto + enlace.
        ///
        /// Para Informe Individual el backend resuelve los oficios usando
        /// BUZ_Asignaciones_Resultados_Informe. Para actos normales usa el procedimiento histórico.
        /// El cliente no necesita conocer esa diferencia; solo envía idActo.
        /// </summary>
        public async Task<List<OficioDto>> ObtenerOficiosAsync(
            int periodo,
            int idAuditoria,
            int idActo,
            int idColabora,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();

                var q = HttpUtility.ParseQueryString(string.Empty);
                q["periodo"] = periodo.ToString();
                q["idAuditoria"] = idAuditoria.ToString();
                q["idActo"] = idActo.ToString();
                q["idColabora"] = idColabora.ToString();

                var url = $"combos/oficios?{q}";

                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                var body = await rsp.Content.ReadAsStringAsync(ct);

                if (!rsp.IsSuccessStatusCode)
                    throw WrapHttpException("obtener oficios", rsp, body);

                var model = JsonSerializer.Deserialize<List<OficioDto>>(body, _json)
                            ?? new List<OficioDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener oficios. Verifica conectividad con el servidor.");
            }
        }

        public async Task<PagedResult<RequerimientoDto>> ObtenerRequerimientosAsync(
            int periodo,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idColabora,
            int skip = 0,
            int take = 100,
            string? search = null,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();

                var q = HttpUtility.ParseQueryString(string.Empty);
                q["periodo"] = periodo.ToString();
                q["idAuditoria"] = idAuditoria.ToString();
                q["idActo"] = idActo.ToString();
                q["idOficio"] = idOficio.ToString();
                q["idColabora"] = idColabora.ToString();
                q["skip"] = skip.ToString();
                q["take"] = take.ToString();
                if (!string.IsNullOrWhiteSpace(search))
                    q["search"] = search;

                var url = $"combos/requerimientos?{q}";

                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                var body = await rsp.Content.ReadAsStringAsync(ct);

                if (!rsp.IsSuccessStatusCode)
                    throw WrapHttpException("obtener requerimientos", rsp, body);

                var model = JsonSerializer.Deserialize<PagedResult<RequerimientoDto>>(body, _json)
                            ?? new PagedResult<RequerimientoDto>(Array.Empty<RequerimientoDto>(), 0);

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener requerimientos. Verifica conectividad con el servidor.");
            }
        }

        public async Task<IReadOnlyList<ArchivoLocalDto>> ObtenerArchivosLocalesAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();

                var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
                q["rfcOrigen"] = rfcOrigen?.Trim().ToUpperInvariant();
                q["requerimientoId"] = requerimientoId.ToString();
                q["colaboraId"] = colaboraId.ToString();

                var url = $"combos/archivos-locales?{q}";

                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync(ct);
                    throw WrapHttpException("obtener archivos locales", rsp, bodyErr);
                }

                var model = await rsp.Content.ReadFromJsonAsync<List<ArchivoLocalDto>>(_json, cancellationToken: ct)
                            ?? new List<ArchivoLocalDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener archivos locales. Verifica conectividad con el servidor.");
            }
        }

        public async Task<IReadOnlyList<AnexoCedulaDto>> ObtenerAnexosCedulaAsync(
            int periodo,
            int idAuditoria,
            int idColabora,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();

                var url = $"combos/anexos-cedulas?periodo={periodo}&idAuditoria={idAuditoria}&idColabora={idColabora}";
                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized && await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync(ct);
                    throw WrapHttpException("obtener anexos de cédula", rsp, bodyErr);
                }

                var wire = await rsp.Content.ReadFromJsonAsync<List<AnexoCedulaWireDto>>(_json, cancellationToken: ct)
                           ?? new List<AnexoCedulaWireDto>();

                var model = wire
                    .Select(x => new AnexoCedulaDto(
                        ID_Anexo: x.IdAnexo,
                        Anexo_Nombre: x.AnexoNombre ?? "",
                        Anexo_Ruta: x.AnexoRuta ?? "",
                        ID_Evento: x.IdEvento,
                        Num_Resultados_Cedula: x.NumResultadosCedula
                    ))
                    .ToList();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener anexos de cédula. Verifica conectividad con el servidor.");
            }
        }

        private sealed record AnexoCedulaWireDto(
                int IdAnexo,
                string? AnexoNombre,
                string? AnexoRuta,
                int IdEvento,
                int NumResultadosCedula
        );

        private sealed record ResultadoCedulaWireDto(
            int IdResultado,
            int? IdAnexo,
            int NumResultado,
            string? Resumen,
            int NumeroEvidencias
        );

        public async Task<IReadOnlyList<ResultadoCedulaDto>> ObtenerResultadosCedulaAsync(
                int periodo,
                int idAuditoria,
                int idEnlace,
                CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                throw new ArgumentException("idAuditoria debe ser mayor a cero.", nameof(idAuditoria));

            if (idEnlace <= 0)
                throw new ArgumentException("idEnlace debe ser mayor a cero.", nameof(idEnlace));

            try
            {
                EnsureAuthHeader();

                var url = $"combos/resultados-cedulas?periodo={periodo}&idAuditoria={idAuditoria}&idEnlace={idEnlace}";
                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized && await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync(ct);
                    throw WrapHttpException("obtener resultados de cédula", rsp, bodyErr);
                }

                var wire = await rsp.Content.ReadFromJsonAsync<List<ResultadoCedulaWireDto>>(_json, cancellationToken: ct)
                           ?? new List<ResultadoCedulaWireDto>();

                var model = wire
                    .Select(x => new ResultadoCedulaDto(
                        ID: x.IdResultado,
                        ID_Anexo: x.IdAnexo ?? 0,
                        Num_Resultado: x.NumResultado,
                        Resumen: x.Resumen,
                        Total_Evidencias: x.NumeroEvidencias
                    ))
                    .ToList();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener resultados de cédula. Verifica conectividad con el servidor.");
            }
        }

        private sealed record ResultadoInformeWireDto(
            int IdResultado,
            int? IdOficio,
            int? IdAuditoria,
            int? IdColabora,
            int NumResultado,
            string? TextoResultado,
            string? ApartadoRubro,
            int NumeroEvidencias,
            int? IdResultadoActo16,
            bool EsCapturadoManual,
            string? OrigenResultado
        );

        /// <summary>
        /// Obtiene los resultados asignados del acto 21 para llenar la tabla principal.
        /// Cada ResultadoInformeDto devuelto representa una fila que puede recibir documentos.
        /// El backend devuelve metadata de origen para distinguir resultados que provienen del
        /// acto 16 contra resultados capturados manualmente en el acto 21.
        /// </summary>
        public async Task<IReadOnlyList<ResultadoInformeDto>> ObtenerResultadosInformeIndividualAsync(
            int periodo,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idColabora,
            CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                throw new ArgumentException("idAuditoria debe ser mayor a cero.", nameof(idAuditoria));

            if (idOficio <= 0)
                throw new ArgumentException("idOficio debe ser mayor a cero.", nameof(idOficio));

            if (idColabora <= 0)
                throw new ArgumentException("idColabora debe ser mayor a cero.", nameof(idColabora));

            try
            {
                EnsureAuthHeader();

                var q = HttpUtility.ParseQueryString(string.Empty);
                q["periodo"] = periodo.ToString();
                q["idAuditoria"] = idAuditoria.ToString();
                q["idActo"] = idActo.ToString();
                q["idOficio"] = idOficio.ToString();
                q["idColabora"] = idColabora.ToString();

                var url = $"combos/resultados-informe-individual?{q}";
                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized && await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync(ct);
                    throw WrapHttpException("obtener resultados del Informe Individual de Auditoría", rsp, bodyErr);
                }

                var wire = await rsp.Content.ReadFromJsonAsync<List<ResultadoInformeWireDto>>(_json, cancellationToken: ct)
                           ?? new List<ResultadoInformeWireDto>();

                var model = wire
                    .Select(x => new ResultadoInformeDto(
                        ID: x.IdResultado,
                        ID_Oficio: x.IdOficio ?? idOficio,
                        ID_Auditoria: x.IdAuditoria ?? idAuditoria,
                        ID_Colabora: x.IdColabora ?? idColabora,
                        Num_Resultado: x.NumResultado,
                        Texto_Resultado: x.TextoResultado ?? string.Empty,
                        Apartado_Rubro: x.ApartadoRubro ?? string.Empty,
                        Total_Documentos: x.NumeroEvidencias,
                        ID_Resultado_Acto16: x.IdResultadoActo16,
                        EsCapturadoManual: x.EsCapturadoManual,
                        OrigenResultado: x.OrigenResultado ?? string.Empty
                    ))
                    .ToList();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener resultados del Informe Individual de Auditoría. Verifica conectividad con el servidor.");
            }
        }

        public async Task<IReadOnlyList<ArchivoLocalDto>> ObtenerArchivosLocalesPorResultadoAsync(
            int resultadoId,
            int colaboraId,
            int movimientoId,
            CancellationToken ct = default)
        {
            if (resultadoId <= 0)
                throw new ArgumentException("resultadoId debe ser mayor a cero.", nameof(resultadoId));
            if (colaboraId <= 0)
                throw new ArgumentException("colaboraId debe ser mayor a cero.", nameof(colaboraId));
            if (movimientoId <= 0)
                throw new ArgumentException("movimientoId debe ser mayor a cero.", nameof(movimientoId));

            try
            {
                EnsureAuthHeader();

                var q = HttpUtility.ParseQueryString(string.Empty);
                q["resultadoId"] = resultadoId.ToString();
                q["colaboraId"] = colaboraId.ToString();
                q["movimientoId"] = movimientoId.ToString();

                var url = $"combos/archivos-locales-resultado?{q}";
                var rsp = await _http.GetAsync(url, ct);

                if (rsp.StatusCode == HttpStatusCode.Unauthorized &&
                    await IntentarRefreshAsync(ct))
                {
                    rsp.Dispose();
                    EnsureAuthHeader();
                    rsp = await _http.GetAsync(url, ct);
                }

                if (!rsp.IsSuccessStatusCode)
                {
                    var bodyErr = await rsp.Content.ReadAsStringAsync(ct);
                    throw WrapHttpException("obtener archivos locales por resultado", rsp, bodyErr);
                }

                var model = await rsp.Content.ReadFromJsonAsync<List<ArchivoLocalDto>>(_json, cancellationToken: ct)
                            ?? new List<ArchivoLocalDto>();

                return model;
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Tiempo de espera agotado al obtener archivos por resultado. Verifica conectividad con el servidor.");
            }
        }

    }

    public sealed record AnioDto(int Id, string Nombre);

    public sealed record EnteDto
    {
        public int IdColabora { get; init; }

        public string IdAuditoria { get; init; } = string.Empty;

        public string EnteFiscalizado { get; init; } = string.Empty;

        public string? Nombre { get; init; }

        public string AuditoriaDisplay =>
            string.IsNullOrWhiteSpace(IdAuditoria)
                ? (Nombre ?? EnteFiscalizado)
                : string.IsNullOrWhiteSpace(Nombre)
                    ? IdAuditoria
                    : $"{IdAuditoria} - {Nombre}";
    }

    public sealed record ActoDto(int ID_Acto, string Nom_Acto);

    public sealed record OficioDto(int IdOficio, string Numero, string Descripcion);

    public sealed record RequerimientoDto(
        int IdRequerimiento,
        string Clave,
        string Descripcion,
        int Total_Documentos);

    public sealed record ArchivoLocalDto(
        int IdArchivoLocal,
        string NombreArchivo,
        string Extension,
        string Tamano,
        DateTime FechaAlta,
        string RutaFisica)
    {
        public string TamanioDescripcion
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Tamano))
                    return string.Empty;

                if (double.TryParse(
                        Tamano,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var bytes)
                    && bytes >= 0)
                {
                    var mb = bytes / 1024d / 1024d;
                    return $"{mb:0.00} MB";
                }

                return Tamano;
            }
        }
    }

    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

    public sealed record AnexoCedulaDto(
        int ID_Anexo,
        string Anexo_Nombre,
        string Anexo_Ruta,
        int ID_Evento,
        int Num_Resultados_Cedula
    );

    public sealed record ResultadoCedulaDto(
        int ID,
        int ID_Anexo,
        int? Num_Resultado,
        string? Resumen,
        int Total_Evidencias
    );

    public sealed record ResultadoInformeDto(
        int ID,
        int ID_Oficio,
        int ID_Auditoria,
        int ID_Colabora,
        int? Num_Resultado,
        string? Texto_Resultado,
        string? Apartado_Rubro,
        int Total_Documentos,
        int? ID_Resultado_Acto16 = null,
        bool EsCapturadoManual = false,
        string? OrigenResultado = null
    )
    {
        public string DescripcionResultado
        {
            get
            {
                var numero = Num_Resultado.HasValue && Num_Resultado.Value > 0
                    ? Num_Resultado.Value.ToString() + " - "
                    : string.Empty;

                return numero + (Texto_Resultado ?? string.Empty);
            }
        }
    }
}
