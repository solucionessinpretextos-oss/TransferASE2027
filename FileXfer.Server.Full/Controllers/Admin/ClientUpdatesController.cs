// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/Admin/ClientUpdatesController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador de actualización de cliente.
// Interacción: Permite consultar versión disponible y metadata de paquete para update.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Linq;

namespace FileXfer.Server.Controllers.Admin
{
    [ApiController]
    [Route("admin/client-updates")]
    public sealed class ClientUpdatesController : ControllerBase
    {
        private readonly ClientUpdatesOptions _options;

        public ClientUpdatesController(IOptionsSnapshot<ClientUpdatesOptions> options)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        [HttpGet("{clientId}")]
        public ActionResult<ClientUpdateInfoDto> GetClientUpdate(
            [FromRoute] string clientId,
            [FromQuery] string? currentVersion)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return BadRequest("El parámetro 'clientId' es obligatorio.");
            }

            if (!_options.Clients.TryGetValue(clientId, out var entry))
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(currentVersion) &&
                !Version.TryParse(currentVersion, out _))
            {
                return BadRequest("El parámetro 'currentVersion' no tiene un formato de versión válido (ejemplo: 1.0.3.0).");
            }

            if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                !downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(entry.LatestVersion, out _) ||
                (!string.IsNullOrWhiteSpace(entry.MinSupportedVersion) &&
                 !Version.TryParse(entry.MinSupportedVersion, out _)) ||
                string.IsNullOrWhiteSpace(entry.Sha256) ||
                entry.Sha256.Length != 64 ||
                !entry.Sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(entry.SignatureBase64) ||
                !entry.SignatureAlgorithm.Equals("RSA-PSS-SHA256", StringComparison.Ordinal))
            {
                return Problem(
                    title: "La publicación de actualización no está firmada correctamente.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                if (Convert.FromBase64String(entry.SignatureBase64).Length < 256)
                    throw new FormatException();
            }
            catch (FormatException)
            {
                return Problem(
                    title: "La firma de la actualización no tiene un formato válido.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var dto = new ClientUpdateInfoDto
            {
                ClientId = clientId,
                LatestVersion = entry.LatestVersion,
                MinSupportedVersion = entry.MinSupportedVersion,
                DownloadUrl = entry.DownloadUrl,
                Sha256 = entry.Sha256.ToLowerInvariant(),
                SignatureBase64 = entry.SignatureBase64,
                SignatureAlgorithm = entry.SignatureAlgorithm,
                ReleaseNotes = entry.ReleaseNotes,
                IsCritical = entry.IsCritical
            };

            return Ok(dto);
        }
    }
}
