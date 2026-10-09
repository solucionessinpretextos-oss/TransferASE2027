// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/CombosController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador de catálogos de selección.
// Interacción: Entrega años, entes, actos, oficios, requerimientos y resultados al selector WPF.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Notas específicas:
// - Los combos deben mantenerse como contratos estables: la UI depende de IDs y descripciones coherentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Services;           // <--- IMPORTANTE
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FileXfer.Server.Dtos.Combos;


// ============================================================================
//  API REST de catálogos para Transfer ASE
// ============================================================================
//  El cliente WPF no consulta la base directamente. Todas las listas desplegables
//  viajan por este controlador:
//      Año -> Entes/Auditorías -> Actos -> Oficios -> Requerimientos/Resultados.
//
//  Las llamadas están protegidas con JWT. Si se prueban desde Swagger sin usar
//  Authorization: Bearer <token>, el resultado correcto será 401 Unauthorized.
// ============================================================================
namespace FileXfer.Server.Controllers
{
    [ApiController]
    [Route("combos")]
    [Authorize] // Si quieres permitir anónimo, cambia a [AllowAnonymous] o comenta esto
    public sealed class CombosController : ControllerBase
    {
        private readonly IBudiDevRepository _repo;

        public CombosController(IBudiDevRepository repo)
        {
            _repo = repo;
        }

        [HttpGet("anios")]
        [ProducesResponseType(typeof(IEnumerable<AnioDto>), 200)]
        public async Task<ActionResult<IEnumerable<AnioDto>>> GetYears(
            [FromQuery] int cuantos = 11,
            CancellationToken ct = default)
        {
            if (cuantos is < 1 or > 50)
                return BadRequest("cuantos debe estar entre 1 y 50.");

            var data = await _repo.GetYearsAsync(cuantos, ct);
            return Ok(data);
        }

        [HttpGet("entes")]
        [ProducesResponseType(typeof(IEnumerable<EnteDto>), 200)]
        public async Task<ActionResult<IEnumerable<EnteDto>>> GetEntes(
            [FromQuery] string rfc,
            [FromQuery] int periodo,
            CancellationToken ct = default)
        {
            var authenticatedRfc = GetAuthenticatedRfc();
            if (authenticatedRfc is null)
                return Unauthorized();
            if (!string.IsNullOrWhiteSpace(rfc) &&
                !string.Equals(rfc.Trim(), authenticatedRfc, System.StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var data = await _repo.GetEntesAsync(authenticatedRfc, periodo, ct);
            return Ok(data);
        }

        [HttpGet("actos")]
        [ProducesResponseType(typeof(IEnumerable<ActoDto>), 200)]
        public async Task<ActionResult<IEnumerable<ActoDto>>> GetActos(
            CancellationToken ct = default)
        {
            var data = await _repo.GetActosAsync(ct);
            return Ok(data);
        }

        // Devuelve oficios para actos normales y para Informe Individual.
        // La diferencia de origen de datos no vive en el controlador; se delega al repositorio
        // para mantener una sola regla de negocio en BudiDevRepository.GetOficiosAsync.
        [HttpGet("oficios")]
        [ProducesResponseType(typeof(IEnumerable<OficioDto>), 200)]
        public async Task<ActionResult<IEnumerable<OficioDto>>> GetOficios(
            [FromQuery] int periodo,
            [FromQuery] int idAuditoria,
            [FromQuery] int idActo,
            [FromQuery] int idColabora,
            CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                return BadRequest("idAuditoria debe ser mayor a cero.");

            if (idActo <= 0)
                return BadRequest("idActo debe ser mayor a cero.");

            if (idColabora <= 0)
                return BadRequest("idColabora debe ser mayor a cero.");

            var rfc = GetAuthenticatedRfc();
            if (rfc is null)
                return Unauthorized();
            if (!await _repo.IsAuditContextAuthorizedAsync(rfc, periodo, idColabora, idAuditoria, ct))
                return Forbid();

            var data = await _repo.GetOficiosAsync(idAuditoria, idActo, idColabora, ct);
            return Ok(data);
        }

        [HttpGet("requerimientos")]
        [ProducesResponseType(typeof(PagedResult<RequerimientoDto>), 200)]
        public async Task<ActionResult<PagedResult<RequerimientoDto>>> GetRequerimientos(
            [FromQuery] int periodo,
            [FromQuery] int idAuditoria,
            [FromQuery] int idActo,
            [FromQuery] int idOficio,
            [FromQuery] int idColabora,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            [FromQuery] string? search = null,
            CancellationToken ct = default)
        {
            if (idOficio <= 0)
                return BadRequest("idOficio debe ser mayor a cero.");

            if (idColabora <= 0)
                return BadRequest("idColabora debe ser mayor a cero.");

            if (skip < 0)
                return BadRequest("skip no puede ser negativo.");
            if (take is < 1 or > 200)
                return BadRequest("take debe estar entre 1 y 200.");
            if (search?.Length > 200)
                return BadRequest("search excede 200 caracteres.");

            var rfc = GetAuthenticatedRfc();
            if (rfc is null)
                return Unauthorized();
            if (!await _repo.IsOficioContextAuthorizedAsync(
                    rfc, periodo, idColabora, idAuditoria, idActo, idOficio, ct))
                return Forbid();

            var data = await _repo.GetRequerimientosAsync(idOficio, idColabora, skip, take, search, ct);
            return Ok(data);
        }

        [HttpGet("anexos-cedulas")]
        [ProducesResponseType(typeof(IEnumerable<AnexoCedulaDto>), 200)]
        public async Task<ActionResult<IEnumerable<AnexoCedulaDto>>> GetAnexosCedulas(
            [FromQuery] int periodo,
            [FromQuery] int idAuditoria,
            [FromQuery] int idColabora,
            CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                return BadRequest("idAuditoria debe ser mayor a cero.");

            var rfc = GetAuthenticatedRfc();
            if (rfc is null)
                return Unauthorized();
            if (!await _repo.IsAuditContextAuthorizedAsync(rfc, periodo, idColabora, idAuditoria, ct))
                return Forbid();

            var data = await _repo.GetAnexosCedulasPorAuditoriaAsync(idAuditoria, ct);
            return Ok(data);
        }

        [HttpGet("resultados-cedulas")]
        [ProducesResponseType(typeof(IEnumerable<ResultadoCedulaDto>), 200)]
        public async Task<ActionResult<IEnumerable<ResultadoCedulaDto>>> GetResultadosCedulas(
            [FromQuery] int periodo,
            [FromQuery] int idAuditoria,
            [FromQuery] int idEnlace,
            CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                return BadRequest("idAuditoria debe ser mayor a cero.");

            var rfc = GetAuthenticatedRfc();
            if (rfc is null)
                return Unauthorized();
            if (!await _repo.IsAuditContextAuthorizedAsync(rfc, periodo, idEnlace, idAuditoria, ct))
                return Forbid();

            if (idEnlace > 0)
            {
                var dataFiltrado = await _repo.GetResultadosCedulasPorAuditoriaAsync(idAuditoria, idEnlace, ct);
                return Ok(dataFiltrado);
            }

            var data = await _repo.GetResultadosCedulasPorAuditoriaAsync(idAuditoria, idEnlace, ct);
            return Ok(data);
        }

        // Endpoint específico del acto 21.
        // Devuelve resultados asignados al enlace en un oficio concreto. El cliente WPF lo usa
        // para llenar la tabla No. Resultado / Texto Resultado / Documentos Anexados.
        [HttpGet("resultados-informe-individual")]
        [ProducesResponseType(typeof(IEnumerable<ResultadoInformeDto>), 200)]
        public async Task<ActionResult<IEnumerable<ResultadoInformeDto>>> GetResultadosInformeIndividual(
            [FromQuery] int periodo,
            [FromQuery] int idAuditoria,
            [FromQuery] int idActo,
            [FromQuery] int idOficio,
            [FromQuery] int idColabora,
            CancellationToken ct = default)
        {
            if (idAuditoria <= 0)
                return BadRequest("idAuditoria debe ser mayor a cero.");

            if (idOficio <= 0)
                return BadRequest("idOficio debe ser mayor a cero.");

            if (idColabora <= 0)
                return BadRequest("idColabora debe ser mayor a cero.");

            var rfc = GetAuthenticatedRfc();
            if (rfc is null)
                return Unauthorized();
            if (!await _repo.IsOficioContextAuthorizedAsync(
                    rfc, periodo, idColabora, idAuditoria, idActo, idOficio, ct))
                return Forbid();

            var data = await _repo.GetResultadosInformeIndividualAsync(idAuditoria, idOficio, idColabora, ct);
            return Ok(data);
        }

        [HttpGet("archivos-locales")]
        [ProducesResponseType(typeof(IReadOnlyList<ArchivoLocalDto>), 200)]
        public async Task<ActionResult<IReadOnlyList<ArchivoLocalDto>>> GetArchivosLocales(
            [FromQuery] string rfcOrigen,
            [FromQuery] int requerimientoId,
            [FromQuery] int colaboraId,
            CancellationToken ct = default)
        {
            var authenticatedRfc = GetAuthenticatedRfc();
            if (authenticatedRfc is null)
                return Unauthorized();
            if (!string.IsNullOrWhiteSpace(rfcOrigen) &&
                !string.Equals(rfcOrigen.Trim(), authenticatedRfc, System.StringComparison.OrdinalIgnoreCase))
                return Forbid();

            if (requerimientoId <= 0)
                return BadRequest("requerimientoId debe ser mayor a cero.");

            if (colaboraId <= 0)
                return BadRequest("colaboraId debe ser mayor a cero.");

            var archivos = await _repo.GetArchivosLocalesAsync(
                authenticatedRfc,
                requerimientoId,
                colaboraId,
                ct);

            return Ok(archivos);
        }

        [HttpGet("archivos-locales-resultado")]
        [ProducesResponseType(typeof(IReadOnlyList<ArchivoLocalDto>), 200)]
        public async Task<ActionResult<IReadOnlyList<ArchivoLocalDto>>> GetArchivosLocalesPorResultado(
            [FromQuery] int resultadoId,
            [FromQuery] int colaboraId,
            [FromQuery] int movimientoId,
            CancellationToken ct = default)
        {
            var authenticatedRfc = GetAuthenticatedRfc();
            if (authenticatedRfc is null)
                return Unauthorized();

            if (resultadoId <= 0)
                return BadRequest("resultadoId debe ser mayor a cero.");
            if (colaboraId <= 0 || movimientoId <= 0)
                return BadRequest("colaboraId y movimientoId deben ser mayores a cero.");

            var archivos = await _repo.GetArchivosLocalesPorResultadoAsync(
                authenticatedRfc,
                resultadoId,
                colaboraId,
                movimientoId,
                ct);
            return Ok(archivos);
        }

        private string? GetAuthenticatedRfc()
        {
            var rfc = User.FindFirst("rfc")?.Value;
            return string.IsNullOrWhiteSpace(rfc) ? null : rfc.Trim().ToUpperInvariant();
        }
    }
}
