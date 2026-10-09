// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/AuditController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador HTTP REST: expone endpoints consumidos por el cliente WPF, el dashboard o herramientas administrativas.
// Interacción: Recibe solicitudes HTTP, valida autorización cuando aplica, delega reglas de negocio a servicios/repositorios y devuelve DTOs estables.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers;

[ApiController]
[Route("audit")]
[Authorize(Policy = "PrivilegedMfa")]
public sealed class AuditController: ControllerBase
{
  private readonly IDbContextFactory<FileXferDbContext> _dbf;
  public AuditController(IDbContextFactory<FileXferDbContext> dbf){ _dbf=dbf; }

  [HttpGet("logs")]
  public async Task<IActionResult> Logs([FromQuery] string? rfc,[FromQuery] string? action,[FromQuery] DateTimeOffset? from,[FromQuery] DateTimeOffset? to,[FromQuery] int page=1,[FromQuery] int pageSize=100){
    if(page<1) return BadRequest(new{message="page debe ser mayor o igual a 1"});
    if(pageSize is <1 or >500) return BadRequest(new{message="pageSize debe estar entre 1 y 500"});
    if(rfc?.Length>13 || action?.Length>128) return BadRequest(new{message="Filtro fuera de tamaño"});
    if(from.HasValue && to.HasValue && from>to) return BadRequest(new{message="El rango de fechas es inválido"});
    await using var db=await _dbf.CreateDbContextAsync();
    var q=db.AuditLogs.AsQueryable();
    if(!string.IsNullOrWhiteSpace(rfc)) q=q.Where(x=>x.ActorRfc==rfc);
    if(!string.IsNullOrWhiteSpace(action)) q=q.Where(x=>x.Action==action);
    if(from!=null) q=q.Where(x=>x.TimestampUtc>=from);
    if(to!=null) q=q.Where(x=>x.TimestampUtc<=to);
    var total=await q.CountAsync();
    var items=await q.OrderByDescending(x=>x.TimestampUtc).Skip((page-1)*pageSize).Take(pageSize).ToListAsync();
    return Ok(new{total,items});
  }
}
