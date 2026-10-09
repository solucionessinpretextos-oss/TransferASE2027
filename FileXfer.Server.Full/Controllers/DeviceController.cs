// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/DeviceController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador HTTP REST: expone endpoints consumidos por el cliente WPF, el dashboard o herramientas administrativas.
// Interacción: Recibe solicitudes HTTP, valida autorización cuando aplica, delega reglas de negocio a servicios/repositorios y devuelve DTOs estables.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace FileXfer.Server.Controllers;

[ApiController]
[Route("auth/device")]
[Authorize]
public sealed class DeviceController: ControllerBase
{
  private readonly IDbContextFactory<FileXferDbContext> _dbf;
  private readonly DevicePolicyOptions _policy;
  public DeviceController(
      IDbContextFactory<FileXferDbContext> dbf,
      IOptions<DevicePolicyOptions> policy)
  {
    _dbf=dbf;
    _policy=policy.Value;
  }

  public sealed record EnrollReq(string deviceId,string publicKeyPem,string? friendlyName);

  [HttpPost("enroll")]
  [RequestSizeLimit(16 * 1024)]
  public async Task<IActionResult> Enroll([FromBody] EnrollReq req){
    var rfc=User.FindFirst("rfc")?.Value;
    if(string.IsNullOrWhiteSpace(rfc)) return Unauthorized();
    rfc=rfc.Trim().ToUpperInvariant();

    var deviceId=req.deviceId?.Trim() ?? "";
    var friendlyName=req.friendlyName?.Trim() ?? "";
    if(deviceId.Length is < 1 or > 64)
      return BadRequest(new{message="deviceId debe contener entre 1 y 64 caracteres."});
    if(friendlyName.Length > 128)
      return BadRequest(new{message="friendlyName excede 128 caracteres."});
    if(string.IsNullOrWhiteSpace(req.publicKeyPem) || req.publicKeyPem.Length > 8192)
      return BadRequest(new{message="La clave pública está vacía o excede el límite permitido."});
    var tokenDeviceId=User.FindFirst("deviceId")?.Value;
    if(!string.Equals(tokenDeviceId,deviceId,StringComparison.Ordinal))
      return Forbid();
    if(!HasRecentFullAuthentication())
      return StatusCode(StatusCodes.Status403Forbidden,new{message="Se requiere autenticación reciente con e.firma."});

    try
    {
      using var rsa=RSA.Create();
      rsa.ImportFromPem(req.publicKeyPem);
      if(rsa.KeySize < Math.Max(2048, _policy.MinRsaBits))
        return BadRequest(new{message="La clave RSA del dispositivo es demasiado corta."});
    }
    catch(Exception ex) when (ex is CryptographicException or ArgumentException)
    {
      return BadRequest(new{message="La clave pública RSA no tiene un formato válido."});
    }

    await using var db=await _dbf.CreateDbContextAsync();
    var dev=await db.Devices.SingleOrDefaultAsync(x=>x.Rfc==rfc && x.DeviceId==deviceId);
    if(dev==null){
      if(await db.Devices.CountAsync(x=>x.Rfc==rfc && !x.Revoked) >= 10)
        return Conflict(new{message="Se alcanzó el máximo de 10 dispositivos activos."});
      db.Devices.Add(new Device{Id=Guid.NewGuid(),Rfc=rfc,DeviceId=deviceId,PublicKeyPem=req.publicKeyPem,FriendlyName=friendlyName});
    } else {
      if(dev.Revoked)
        return Conflict(new{message="El dispositivo fue revocado y no puede reactivarse mediante enrolamiento ordinario."});
      dev.PublicKeyPem=req.publicKeyPem;
      dev.FriendlyName=string.IsNullOrWhiteSpace(friendlyName) ? dev.FriendlyName : friendlyName;
    }
    await db.SaveChangesAsync();
    return Ok(new{ok=true});
  }

  [HttpPost("revoke")]
  public async Task<IActionResult> Revoke([FromQuery] string deviceId){
    var rfc=User.FindFirst("rfc")?.Value;
    if(string.IsNullOrWhiteSpace(rfc)) return Unauthorized();
    rfc=rfc.Trim().ToUpperInvariant();
    deviceId=deviceId?.Trim() ?? "";
    if(deviceId.Length is < 1 or > 64) return BadRequest();
    await using var db=await _dbf.CreateDbContextAsync();
    var dev=await db.Devices.SingleOrDefaultAsync(x=>x.Rfc==rfc && x.DeviceId==deviceId);
    if(dev==null) return NotFound();
    dev.Revoked=true; dev.RevokedUtc=DateTimeOffset.UtcNow;
    var tokens=await db.RefreshTokens
      .Where(x=>x.Rfc==rfc && x.DeviceId==deviceId && !x.IsRevoked)
      .ToListAsync();
    foreach(var token in tokens) token.IsRevoked=true;
    await db.SaveChangesAsync();
    return Ok(new{ok=true});
  }

  [HttpGet("list")]
  public async Task<IActionResult> List(){
    var rfc=User.FindFirst("rfc")?.Value;
    if(string.IsNullOrWhiteSpace(rfc)) return Unauthorized();
    rfc=rfc.Trim().ToUpperInvariant();
    await using var db=await _dbf.CreateDbContextAsync();
    var items=await db.Devices.Where(x=>x.Rfc==rfc).OrderByDescending(x=>x.CreatedUtc).ToListAsync();
    return Ok(items.Select(x=>new{x.DeviceId,x.FriendlyName,x.Revoked,x.CreatedUtc,x.RevokedUtc}));
  }

  private bool HasRecentFullAuthentication()
  {
    var amr=User.FindFirst("amr")?.Value;
    var authTimeRaw=User.FindFirst("auth_time")?.Value;
    if(string.IsNullOrWhiteSpace(amr) ||
       !amr.StartsWith("efirma",StringComparison.Ordinal) ||
       !long.TryParse(authTimeRaw,out var authTimeSeconds))
      return false;

    var age=DateTimeOffset.UtcNow-DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds);
    return age>=TimeSpan.Zero && age<=TimeSpan.FromMinutes(5);
  }
}
