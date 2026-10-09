// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/ArchivosLocalesController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador de documentos anexados.
// Interacción: Permite listar y dar baja lógica a documentos previamente cargados por contexto.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Notas específicas:
// - Las operaciones de ver/borrar documentos deben filtrar por ResultadoId + ColaboraId + MovimientoId en acto 21.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Dtos.ArchivosLocales;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


// ============================================================================
//  API REST de consulta y baja de documentos registrados
// ============================================================================
//  La carga física del archivo se realiza por gRPC en UploadGrpcService. Este controlador
//  se encarga de operaciones posteriores de negocio: ver documentos ya registrados y
//  borrar documentos mediante baja lógica en Transfer_ArchivosLocales.
// ============================================================================
namespace FileXfer.Server.Controllers
{
[ApiController]
[Route("archivos-locales")]
[Authorize]
[RequestSizeLimit(128 * 1024)]
public sealed class ArchivosLocalesController : ControllerBase
    {
        private readonly IBudiDevRepository _repo;

        public ArchivosLocalesController(IBudiDevRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        [HttpPost("eliminar")]
        [ProducesResponseType(typeof(EliminarArchivosLocalesResponseDto), 200)]
        public async Task<ActionResult<EliminarArchivosLocalesResponseDto>> Eliminar(
            [FromBody] EliminarArchivosLocalesRequestDto request,
            CancellationToken ct = default)
        {
            if (request is null)
                return BadRequest("Solicitud inválida.");

            var authenticatedRfc = GetAuthenticatedRfc();
            if (authenticatedRfc is null)
                return Unauthorized();
            if (!string.IsNullOrWhiteSpace(request.RfcUsuario) &&
                !string.Equals(request.RfcUsuario.Trim(), authenticatedRfc, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            if (request.IdRequerimiento <= 0)
                return BadRequest("IdRequerimiento debe ser mayor a cero.");

            if (request.IdColabora <= 0)
                return BadRequest("IdColabora debe ser mayor a cero.");

            if (request.IdsArchivos is null || request.IdsArchivos.Count == 0)
                return Ok(new EliminarArchivosLocalesResponseDto { Eliminados = 0 });
            if (request.IdsArchivos.Count > 500)
                return BadRequest("No se pueden eliminar más de 500 archivos por solicitud.");

            request.IdsArchivos = request.IdsArchivos.Where(id => id > 0).Distinct().ToList();

            var eliminados = await _repo.EliminarArchivosLocalesAsync(
                authenticatedRfc,
                request.IdRequerimiento,
                request.IdColabora,
                request.IdsArchivos,
                ct);

            return Ok(new EliminarArchivosLocalesResponseDto
            {
                Eliminados = eliminados
            });
        }

        [HttpPost("eliminar-por-resultado")]
        [ProducesResponseType(typeof(EliminarArchivosLocalesResponseDto), 200)]
        public async Task<ActionResult<EliminarArchivosLocalesResponseDto>> EliminarPorResultado(
            [FromBody] EliminarArchivosLocalesPorResultadoRequestDto request,
            CancellationToken ct = default)
        {
            if (request is null)
                return BadRequest("Solicitud inválida.");

            var authenticatedRfc = GetAuthenticatedRfc();
            if (authenticatedRfc is null)
                return Unauthorized();
            if (!string.IsNullOrWhiteSpace(request.RfcUsuario) &&
                !string.Equals(request.RfcUsuario.Trim(), authenticatedRfc, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            if (request.ResultadoId <= 0)
                return BadRequest("ResultadoId debe ser mayor a cero.");
            if (request.ColaboraId <= 0 || request.MovimientoId <= 0)
                return BadRequest("ColaboraId y MovimientoId deben ser mayores a cero.");

            if (request.IdsArchivos is null || request.IdsArchivos.Count == 0)
                return Ok(new EliminarArchivosLocalesResponseDto { Eliminados = 0 });
            if (request.IdsArchivos.Count > 500)
                return BadRequest("No se pueden eliminar más de 500 archivos por solicitud.");

            request.IdsArchivos = request.IdsArchivos.Where(id => id > 0).Distinct().ToList();

            var eliminados = await _repo.EliminarArchivosLocalesPorResultadoAsync(
                authenticatedRfc,
                request.ResultadoId,
                request.ColaboraId,
                request.MovimientoId,
                request.IdsArchivos,
                ct);

            return Ok(new EliminarArchivosLocalesResponseDto
            {
                Eliminados = eliminados
            });
        }

        private string? GetAuthenticatedRfc()
        {
            var rfc = User.FindFirst("rfc")?.Value;
            return string.IsNullOrWhiteSpace(rfc) ? null : rfc.Trim().ToUpperInvariant();
        }
    }
}
