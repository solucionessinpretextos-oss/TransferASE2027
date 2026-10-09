// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/RfcAdminController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador HTTP REST: expone endpoints consumidos por el cliente WPF, el dashboard o herramientas administrativas.
// Interacción: Recibe solicitudes HTTP, valida autorización cuando aplica, delega reglas de negocio a servicios/repositorios y devuelve DTOs estables.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Data;
using FileXfer.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers;

[ApiController]
[Route("admin/rfcs")]
[Authorize(Policy = "PrivilegedMfa")]
public sealed class RfcAdminController : ControllerBase
{
    private readonly IDbContextFactory<FileXferDbContext> _dbf;

    public RfcAdminController(IDbContextFactory<FileXferDbContext> dbf)
    {
        _dbf = dbf;
    }

    public sealed record RfcUserDto(
        string rfc,
        string? displayName,
        string[] roles,
        bool mfaRequired,
        bool isActive,
        string? notes);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? query, [FromQuery] bool? isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var q = db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var qNorm = query.Trim().ToUpperInvariant();
            q = q.Where(x => x.Rfc.Contains(qNorm) || (x.DisplayName ?? "").Contains(query));
        }

        if (isActive.HasValue)
            q = q.Where(x => x.IsActive == isActive.Value);

        var items = await q
            .OrderBy(x => x.Rfc)
            .Select(x => new RfcUserDto(
                x.Rfc,
                x.DisplayName,
                x.Roles,
                x.MfaRequired,
                x.IsActive,
                x.Notes))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("{rfc}")]
    public async Task<IActionResult> Get(string rfc)
    {
        if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var r))
            return BadRequest(new { message = "RFC inválido" });

        await using var db = await _dbf.CreateDbContextAsync();
        var u = await db.Users.SingleOrDefaultAsync(x => x.Rfc == r);
        if (u == null) return NotFound();

        return Ok(new RfcUserDto(u.Rfc, u.DisplayName, u.Roles, u.MfaRequired, u.IsActive, u.Notes));
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] RfcUserDto dto)
    {
        if (!IdentityInputPolicy.TryNormalizeRfc(dto.rfc, out var r))
            return BadRequest(new { message = "RFC inválido" });
        if (!IdentityInputPolicy.TryNormalizeRoles(dto.roles, out var roles))
            return BadRequest(new { message = "Debe indicar al menos un rol válido: user, ops o admin." });
        if (dto.displayName?.Length > 256 || dto.notes?.Length > 1024)
            return BadRequest(new { message = "El nombre o las notas exceden el tamaño permitido." });
        if (dto.mfaRequired)
            return BadRequest(new { message = "Primero cree al usuario y permita que enrole MFA; después podrá exigirlo." });
        if (HasRole(roles, "admin") || HasRole(roles, "ops"))
        {
            return BadRequest(new
            {
                message = "Cree la cuenta con rol user, complete MFA y después promuévala a un rol privilegiado."
            });
        }

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Users.AnyAsync(x => x.Rfc == r))
            return Conflict(new { message = "RFC ya existe" });

        var now = DateTimeOffset.UtcNow;
        var actor = User.FindFirst("rfc")?.Value ?? User.Identity?.Name ?? "system";

        var entity = new RfcUser
        {
            Id = Guid.NewGuid(),
            Rfc = r,
            DisplayName = dto.displayName,
            Roles = roles,
            MfaRequired = dto.mfaRequired,
            IsActive = dto.isActive,
            Notes = dto.notes,
            CreatedUtc = now,
            CreatedBy = actor
        };

        db.Users.Add(entity);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = now,
            ActorRfc = actor,
            Action = "rfc_provision_create",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Details = $"RFC={r}; Roles={string.Join(",", entity.Roles)}; MfaRequired={entity.MfaRequired}; IsActive={entity.IsActive}"
        });

        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Get),
            new { rfc = r },
            new RfcUserDto(r, entity.DisplayName, entity.Roles, entity.MfaRequired, entity.IsActive, entity.Notes));
    }

    [HttpPut("{rfc}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(string rfc, [FromBody] RfcUserDto dto)
    {
        if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var r))
            return BadRequest(new { message = "RFC inválido" });
        if (!string.IsNullOrWhiteSpace(dto.rfc) &&
            (!IdentityInputPolicy.TryNormalizeRfc(dto.rfc, out var bodyRfc) ||
             !string.Equals(bodyRfc, r, StringComparison.Ordinal)))
        {
            return BadRequest(new { message = "El RFC del cuerpo no coincide con el RFC de la ruta." });
        }
        if (!IdentityInputPolicy.TryNormalizeRoles(dto.roles, out var roles))
            return BadRequest(new { message = "Debe indicar al menos un rol válido: user, ops o admin." });
        if (dto.displayName?.Length > 256 || dto.notes?.Length > 1024)
            return BadRequest(new { message = "El nombre o las notas exceden el tamaño permitido." });

        await using var db = await _dbf.CreateDbContextAsync();
        var u = await db.Users.SingleOrDefaultAsync(x => x.Rfc == r);
        if (u == null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var actor = User.FindFirst("rfc")?.Value ?? User.Identity?.Name ?? "system";
        var removesAdmin = HasRole(u.Roles, "admin") && !HasRole(roles, "admin");

        if (string.Equals(actor, r, StringComparison.OrdinalIgnoreCase) &&
            (!dto.isActive || !HasRole(roles, "admin")))
        {
            return BadRequest(new { message = "No puede desactivar su propia cuenta ni retirarse el rol admin." });
        }

        if (u.IsActive && HasRole(u.Roles, "admin") && (removesAdmin || !dto.isActive) &&
            await CountActiveAdminsAsync(db) <= 1)
        {
            return BadRequest(new { message = "No se puede retirar el último administrador activo." });
        }

        if (dto.mfaRequired && !u.MfaRequired &&
            !await db.MfaSecrets.AnyAsync(x => x.Rfc == r && x.Active))
        {
            return BadRequest(new { message = "El usuario debe enrolar MFA antes de hacerlo obligatorio." });
        }
        if ((HasRole(roles, "admin") || HasRole(roles, "ops")) &&
            (!dto.mfaRequired || !await db.MfaSecrets.AnyAsync(x => x.Rfc == r && x.Active)))
        {
            return BadRequest(new
            {
                message = "Los roles admin y ops requieren MFA activo y obligatorio."
            });
        }

        var securityStateChanged =
            u.IsActive != dto.isActive ||
            u.MfaRequired != dto.mfaRequired ||
            !u.Roles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(roles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        u.DisplayName = dto.displayName;
        u.Roles = roles;
        u.MfaRequired = dto.mfaRequired;
        u.IsActive = dto.isActive;
        u.Notes = dto.notes;
        u.LastModifiedUtc = now;
        u.LastModifiedBy = actor;
        if (securityStateChanged)
        {
            u.SecurityVersion++;
            var activeRefreshTokens = await db.RefreshTokens
                .Where(token => token.Rfc == r && !token.IsRevoked)
                .ToListAsync(HttpContext.RequestAborted);
            foreach (var refreshToken in activeRefreshTokens)
                refreshToken.IsRevoked = true;
        }

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = now,
            ActorRfc = actor,
            Action = "rfc_provision_update",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Details = $"RFC={r}; Roles={string.Join(",", u.Roles)}; MfaRequired={u.MfaRequired}; IsActive={u.IsActive}"
        });

        await db.SaveChangesAsync();

        return Ok(new RfcUserDto(r, u.DisplayName, u.Roles, u.MfaRequired, u.IsActive, u.Notes));
    }

    [HttpPost("{rfc}/deactivate")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Deactivate(string rfc)
    {
        if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var r))
            return BadRequest(new { message = "RFC inválido" });

        await using var db = await _dbf.CreateDbContextAsync();
        var u = await db.Users.SingleOrDefaultAsync(x => x.Rfc == r);
        if (u == null) return NotFound();

        if (!u.IsActive)
            return Ok(new { message = "Ya estaba desactivado" });

        var now = DateTimeOffset.UtcNow;
        var actor = User.FindFirst("rfc")?.Value ?? User.Identity?.Name ?? "system";
        if (string.Equals(actor, r, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "No puede desactivar su propia cuenta." });
        if (HasRole(u.Roles, "admin") && await CountActiveAdminsAsync(db) <= 1)
            return BadRequest(new { message = "No se puede desactivar el último administrador activo." });

        u.IsActive = false;
        u.SecurityVersion++;
        u.LastModifiedUtc = now;
        u.LastModifiedBy = actor;
        var refreshTokens = await db.RefreshTokens
            .Where(token => token.Rfc == r && !token.IsRevoked)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var refreshToken in refreshTokens)
            refreshToken.IsRevoked = true;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = now,
            ActorRfc = actor,
            Action = "rfc_provision_deactivate",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Details = $"RFC={r}"
        });

        await db.SaveChangesAsync();

        return Ok(new { message = "Desactivado", rfc = r });
    }

    [HttpPost("{rfc}/activate")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Activate(string rfc)
    {
        if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var r))
            return BadRequest(new { message = "RFC inválido" });

        await using var db = await _dbf.CreateDbContextAsync();
        var u = await db.Users.SingleOrDefaultAsync(x => x.Rfc == r);
        if (u == null) return NotFound();

        if (u.IsActive)
            return Ok(new { message = "Ya estaba activo" });

        var now = DateTimeOffset.UtcNow;
        var actor = User.FindFirst("rfc")?.Value ?? User.Identity?.Name ?? "system";

        u.IsActive = true;
        u.SecurityVersion++;
        u.LastModifiedUtc = now;
        u.LastModifiedBy = actor;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = now,
            ActorRfc = actor,
            Action = "rfc_provision_activate",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Details = $"RFC={r}"
        });

        await db.SaveChangesAsync();

        return Ok(new { message = "Activado", rfc = r });
    }

    private static bool HasRole(IEnumerable<string>? roles, string expected) =>
        (roles ?? Array.Empty<string>()).Any(role =>
            string.Equals(role, expected, StringComparison.OrdinalIgnoreCase));

    private static async Task<int> CountActiveAdminsAsync(FileXferDbContext db)
    {
        var roleSets = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => user.Roles)
            .ToListAsync();
        return roleSets.Count(roles => HasRole(roles, "admin"));
    }
}
