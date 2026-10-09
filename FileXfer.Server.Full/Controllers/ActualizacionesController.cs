// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/ActualizacionesController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador legacy/compatible de actualizaciones.
// Interacción: Mantiene compatibilidad con llamadas existentes del cliente para detectar updates.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Dtos.Actualizaciones;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FileXfer.Server.Controllers
{
    [ApiController]
    [Route("actualizaciones")]
    public sealed class ActualizacionesController : ControllerBase
    {
        private readonly IConfiguration _config;

        public ActualizacionesController(IConfiguration config)
        {
            _config = config;
        }

        [HttpGet("transfer-cliente")]
        [ProducesResponseType(typeof(ActualizacionClienteDto), 200)]
        [ProducesResponseType(204)]
        public ActionResult<ActualizacionClienteDto> GetUltimaVersion(
            [FromQuery] string clienteId = "TransferClienteASE",
            [FromQuery] string? versionLocal = null)
        {
            // Este contrato legado no transporta hash ni firma. Mantenerlo activo permitiría
            // ejecutar paquetes no autenticados. El cliente 2027 usa el manifiesto firmado de
            // /admin/client-updates/{clientId}.
            return StatusCode(StatusCodes.Status410Gone);
        }
    }
}
