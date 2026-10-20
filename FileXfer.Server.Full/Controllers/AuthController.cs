// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/AuthController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador de autenticación con e.firma/JWT/refresh token.
// Interacción: Orquesta nonce, validación de firma y emisión de tokens para cliente WPF.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Security.Cryptography;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Controllers;

[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController : ControllerBase
{
    private readonly IDbContextFactory<FileXferDbContext> _dbf;
    private readonly JwtService _jwt;
    private readonly ISatCertificateValidator _sat;
    private readonly RateLockService _rate;
    private readonly IAlertSink _alerts;
    private readonly ILogger<AuthController> _log;
    private readonly IWebHostEnvironment _env;
    private readonly SatOptions _satOptions;
    private readonly IBudiDevRepository _budi;

    public AuthController(
        IDbContextFactory<FileXferDbContext> dbf,
        JwtService jwt,
        ISatCertificateValidator sat,
        RateLockService rate,
        IAlertSink alerts,
        ILogger<AuthController> log,
        IWebHostEnvironment env,
        IOptions<SatOptions> satOptions,
        IBudiDevRepository budi)
    {
        _dbf = dbf;
        _jwt = jwt;
        _sat = sat;
        _rate = rate;
        _alerts = alerts;
        _log = log;
        _env = env;
        _satOptions = satOptions.Value;
        _budi = budi;
    }

    [HttpPost("nonce")]
    public async Task<IActionResult> Nonce()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_rate.TryRate("nonce:" + ip))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Demasiadas solicitudes de nonce. Intente más tarde." });

        var val = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var db = await _dbf.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;

        // Las solicitudes abandonadas caducan físicamente antes de insertar. El límite global
        // es una defensa adicional ante ataques distribuidos contra la persistencia.
        var expiredQuery = db.Nonces.Where(x => x.ExpiresUtc < now);
        if (db.Database.IsRelational())
        {
            await expiredQuery.ExecuteDeleteAsync(HttpContext.RequestAborted);
        }
        else
        {
            // El proveedor InMemory usado exclusivamente en Development no implementa
            // ExecuteDelete. Producción conserva el DELETE atómico en el motor relacional.
            db.Nonces.RemoveRange(await expiredQuery.ToListAsync(HttpContext.RequestAborted));
            await db.SaveChangesAsync(HttpContext.RequestAborted);
        }

        if (await db.Nonces.CountAsync(HttpContext.RequestAborted) >= 10_000)
        {
            _log.LogWarning("Se alcanzó el límite de nonces activos. IP={IP}", ip);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Servicio de autenticación temporalmente saturado." });
        }

        var n = new Nonce
        {
            ValueBase64 = val,
            ExpiresUtc = now.AddMinutes(5),
            Rfc = null,
            DeviceId = null
        };
        db.Nonces.Add(n);
        await db.SaveChangesAsync();

        return Ok(new
        {
            nonceId = n.Id.ToString("N"),
            nonce = val,
            expiresUtc = n.ExpiresUtc
        });
    }

    public sealed record LoginReq(
        string rfc,
        string cerBase64,
        string nonceId,
        string nonce,
        string signatureVersion,
        string signatureBase64,
        string deviceId);

    public sealed record LoginResp(
        string? token,
        string? rfc,
        string[]? roles,
        string? deviceId,
        string? message,
        string? refreshToken,
        DateTimeOffset? refreshExpiresUtc);

    public sealed record RefreshReq(
        string refreshToken);

    [HttpPost("login-signed")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> Login([FromBody] LoginReq req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        try
        {
            if (!IdentityInputPolicy.TryNormalizeRfc(req.rfc, out var rfc))
                return Problem(title: "RFC inválido", statusCode: 400);

            if (!IdentityInputPolicy.IsValidDeviceId(req.deviceId))
                return Problem(title: "deviceId inválido", statusCode: 400);

            if (!_rate.TryRate("login:ip:" + ip) || !_rate.TryRate("login:rfc:" + rfc))
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { message = "Demasiados intentos de inicio de sesión. Intente más tarde." });

            if (!Guid.TryParseExact(req.nonceId, "N", out var nonceGuid))
            {
                return Problem(title: "nonceId inválido", statusCode: 400);
            }

            await using var db = await _dbf.CreateDbContextAsync();

            var now = DateTimeOffset.UtcNow;
            var nonceQuery = db.Nonces.Where(x =>
                x.Id == nonceGuid &&
                x.ValueBase64 == req.nonce &&
                x.ExpiresUtc >= now);

            int noncesConsumidos;
            if (db.Database.IsRelational())
            {
                noncesConsumidos = await nonceQuery.ExecuteDeleteAsync(HttpContext.RequestAborted);
            }
            else
            {
                var nonce = await nonceQuery.SingleOrDefaultAsync(HttpContext.RequestAborted);
                noncesConsumidos = nonce is null ? 0 : 1;
                if (nonce is not null)
                {
                    db.Nonces.Remove(nonce);
                    await db.SaveChangesAsync(HttpContext.RequestAborted);
                }
            }

            if (noncesConsumidos != 1)
            {
                return Problem(title: "Nonce inválido o expirado", statusCode: 400);
            }

            var devBypassRequested =
                _env.IsDevelopment()
                && _satOptions.AllowDevelopmentBypass
                && !string.IsNullOrWhiteSpace(_satOptions.DevBypassToken)
                && string.Equals(req.signatureBase64, _satOptions.DevBypassToken, StringComparison.Ordinal);

            ISatCertificateValidator.ValidationResult sat;
            if (devBypassRequested)
            {
                sat = new(true, rfc, null, true);
                _log.LogWarning("DEV-BYPASS de SAT activado para RFC={RFC} desde IP={IP}", rfc, ip);
            }
            else
            {

                if (string.IsNullOrWhiteSpace(req.cerBase64) || req.cerBase64.Length > 65_536)
                {
                    return Problem(title: "CER requerido o fuera de tamaño", statusCode: 400);
                }

                if (!string.Equals(req.signatureVersion, LoginSignaturePayload.Version, StringComparison.Ordinal))
                {
                    return Problem(title: "Versión de firma no soportada", statusCode: 400);
                }

                if (string.IsNullOrWhiteSpace(req.signatureBase64))
                {
                    return Problem(title: "Firma de e.firma requerida", statusCode: 400);
                }

                byte[] cerBytes;
                try
                {
                    cerBytes = Convert.FromBase64String(req.cerBase64);
                }
                catch (FormatException fe)
                {
                    _log.LogWarning(fe, "CER Base64 inválido para RFC={RFC} desde IP={IP}", rfc, ip);
                    return Problem(title: "Formato de CER inválido", statusCode: 400);
                }

                sat = _sat.ValidateSignedLogin(
                    rfc,
                    cerBytes,
                    req.nonceId,
                    req.nonce,
                    req.deviceId,
                    req.signatureBase64);

                if (!sat.Ok)
                {
                    await _alerts.NotifySecurityEventAsync(
                        "Login SAT fallido",
                        $"RFC={rfc}; IP={ip}; Error={sat.Error}",
                        HttpContext.RequestAborted);

                    return Problem(title: sat.Error ?? "Certificado inválido", statusCode: 401);
                }

                rfc = sat.NormalizedRfc ?? rfc;
            }

            var user = await db.Users.SingleOrDefaultAsync(x => x.Rfc == rfc);

            if (user == null || !user.IsActive)
            {
                var entes = await _budi.GetEntesAsync(
                    rfc,
                    periodo: 0,
                    HttpContext.RequestAborted);

                var tieneAuditoriaActiva = entes is { Count: > 0 };

                if (!tieneAuditoriaActiva)
                {
                    return Problem(title: "Usuario no autorizado", statusCode: 401);
                }

                if (user == null)
                {
                    var displayName =
                        entes[0].EnteFiscalizado
                        ?? entes[0].Nombre
                        ?? rfc;

                    user = new RfcUser
                    {
                        Id = Guid.NewGuid(),
                        Rfc = rfc,
                        DisplayName = displayName,
                        Roles = new[] { "user" },
                        MfaRequired = false,
                        IsActive = true,
                        Notes = "Alta automática por auditoría digital activa (BuDi).",
                        CreatedUtc = DateTimeOffset.UtcNow,
                        CreatedBy = "auth_auto"
                    };

                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                }
                else
                {
                    return Problem(title: "Usuario no autorizado", statusCode: 401);
                }
            }

            var revokedDevice = await db.Devices.AnyAsync(
                device => device.Rfc == rfc &&
                          device.DeviceId == req.deviceId &&
                          device.Revoked,
                HttpContext.RequestAborted);
            if (revokedDevice)
                return Problem(title: "El dispositivo fue revocado", statusCode: 403);

            var token = _jwt.Issue(
                rfc,
                user.Roles ?? Array.Empty<string>(),
                user.SecurityVersion,
                "efirma",
                req.deviceId);

            var excessRefreshTokens = await db.RefreshTokens
                .Where(existing => existing.Rfc == rfc && !existing.IsRevoked)
                .OrderByDescending(existing => existing.CreatedUtc)
                .Skip(19)
                .ToListAsync(HttpContext.RequestAborted);
            foreach (var oldToken in excessRefreshTokens)
                oldToken.IsRevoked = true;

            var (refreshPlain, refreshEntity) = CreateRefreshToken(
                rfc,
                req.deviceId,
                ip,
                user.SecurityVersion);
            db.RefreshTokens.Add(refreshEntity);

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                ActorRfc = rfc,
                Action = "login_ok",
                Ip = ip,
                Details = $"deviceId={req.deviceId}; satDevelopmentBypass={sat.BypassUsed}"
            });

            await db.SaveChangesAsync();

            return Ok(new
            {
                token = token,
                rfc = rfc,
                roles = user.Roles,
                deviceId = req.deviceId,
                message = "ok",
                refreshToken = refreshPlain,
                refreshExpiresUtc = refreshEntity.ExpiresUtc,
                certificado = sat.InfoCert is null ? null : new
                {
                    rfc = sat.InfoCert.Rfc,
                    curp = sat.InfoCert.Curp,
                    nombreCompleto = sat.InfoCert.NombreCompleto,
                    serie = sat.InfoCert.Serie,
                    notBefore = sat.InfoCert.NotBefore,
                    notAfter = sat.InfoCert.NotAfter,
                    emisor = sat.InfoCert.Emisor,
                    subject = sat.InfoCert.Subject
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error inesperado en /auth/login-signed desde {Ip}", ip);
            return Problem(title: "Error interno", statusCode: 500);
        }
    }

    [HttpPost("refresh")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<IActionResult> Refresh([FromBody] RefreshReq req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_rate.TryRate("refresh:ip:" + ip))
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                new { message = "Demasiadas solicitudes de renovación. Intente más tarde." });

        if (string.IsNullOrWhiteSpace(req.refreshToken) || req.refreshToken.Length > 512)
            return Problem(title: "Refresh token requerido", statusCode: 400);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var hash = ComputeSha256(req.refreshToken);

            await using var db = await _dbf.CreateDbContextAsync();

            var candidate = await db.RefreshTokens
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TokenHash == hash);

            if (candidate == null || candidate.ExpiresUtc <= now || candidate.AbsoluteExpiresUtc <= now)
                return Problem(title: "Refresh token inválido o expirado", statusCode: 401);

            if (candidate.IsRevoked)
            {
                if (candidate.FamilyId != Guid.Empty)
                {
                    if (db.Database.IsRelational())
                    {
                        await db.RefreshTokens
                            .Where(familyToken => familyToken.FamilyId == candidate.FamilyId && !familyToken.IsRevoked)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(familyToken => familyToken.IsRevoked, true),
                                HttpContext.RequestAborted);
                    }
                    else
                    {
                        var family = await db.RefreshTokens
                            .Where(familyToken => familyToken.FamilyId == candidate.FamilyId && !familyToken.IsRevoked)
                            .ToListAsync(HttpContext.RequestAborted);
                        foreach (var familyToken in family)
                            familyToken.IsRevoked = true;
                        await db.SaveChangesAsync(HttpContext.RequestAborted);
                    }
                }

                await _alerts.NotifySecurityEventAsync(
                    "Reutilización de refresh token: familia revocada",
                    $"RFC={candidate.Rfc}; IP={ip}; DeviceId={candidate.DeviceId}; FamilyId={candidate.FamilyId}",
                    HttpContext.RequestAborted);
                return Problem(title: "Refresh token inválido o expirado", statusCode: 401);
            }

            RefreshToken rt;
            if (db.Database.IsRelational())
            {
                var claimed = await db.RefreshTokens
                    .Where(x => x.Id == candidate.Id && !x.IsRevoked && x.ExpiresUtc > now)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(x => x.IsRevoked, true),
                        HttpContext.RequestAborted);
                if (claimed != 1)
                    return Problem(title: "Refresh token inválido o ya utilizado", statusCode: 401);

                rt = candidate;
            }
            else
            {
                rt = await db.RefreshTokens.SingleAsync(x => x.Id == candidate.Id);
                if (rt.IsRevoked || rt.ExpiresUtc <= now)
                    return Problem(title: "Refresh token inválido o ya utilizado", statusCode: 401);
                rt.IsRevoked = true;
            }

            var user = await db.Users.SingleOrDefaultAsync(x => x.Rfc == rt.Rfc);
            if (user == null || !user.IsActive)
            {
                return Problem(title: "Usuario no autorizado", statusCode: 401);
            }

            if (rt.SecurityVersion != user.SecurityVersion)
            {
                return Problem(title: "La sesión requiere autenticación completa", statusCode: 401);
            }

            if (!string.IsNullOrWhiteSpace(rt.DeviceId) &&
                await db.Devices.AnyAsync(
                    device => device.Rfc == user.Rfc &&
                              device.DeviceId == rt.DeviceId &&
                              device.Revoked,
                    HttpContext.RequestAborted))
            {
                return Problem(title: "El dispositivo fue revocado", statusCode: 401);
            }

            var (newRefreshPlain, newRefreshEntity) = CreateRefreshToken(
                user.Rfc,
                rt.DeviceId,
                ip,
                user.SecurityVersion,
                rt.FamilyId,
                rt.Id,
                rt.AbsoluteExpiresUtc);
            db.RefreshTokens.Add(newRefreshEntity);

            var token = _jwt.Issue(
                user.Rfc,
                user.Roles ?? Array.Empty<string>(),
                user.SecurityVersion,
                "efirma",
                rt.DeviceId);

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = now,
                ActorRfc = user.Rfc,
                Action = "refresh_ok",
                Ip = ip,
                Details = $"deviceId={rt.DeviceId}"
            });

            await db.SaveChangesAsync();

            return Ok(new LoginResp(
                token,
                user.Rfc,
                user.Roles,
                deviceId: rt.DeviceId,
                message: "ok",
                refreshToken: newRefreshPlain,
                refreshExpiresUtc: newRefreshEntity.ExpiresUtc));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error inesperado en /auth/refresh desde {Ip}", ip);
            return Problem(title: "Error interno", statusCode: 500);
        }
    }

    private static string ComputeSha256(string raw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash); // .NET 5+
    }

    private static (string plain, RefreshToken entity) CreateRefreshToken(
        string rfc,
        string? deviceId,
        string? ip,
        int securityVersion,
        Guid? familyId = null,
        Guid? parentTokenId = null,
        DateTimeOffset? absoluteExpiresUtc = null,
        TimeSpan? ttl = null)
    {
        var rawBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        var plain = Convert.ToBase64String(rawBytes);

        var now = DateTimeOffset.UtcNow;
        var absoluteExpires = absoluteExpiresUtc ?? now.AddDays(30);
        var expires = now.Add(ttl ?? TimeSpan.FromDays(7));
        if (expires > absoluteExpires)
            expires = absoluteExpires;

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Rfc = rfc,
            TokenHash = ComputeSha256(plain),
            CreatedUtc = now,
            ExpiresUtc = expires,
            AbsoluteExpiresUtc = absoluteExpires,
            FamilyId = familyId ?? Guid.NewGuid(),
            ParentTokenId = parentTokenId,
            SecurityVersion = securityVersion,
            // Columna histórica conservada para una migración no destructiva. El runtime
            // 2027 autentica exclusivamente con e.firma y nunca interpreta este valor.
            MfaVerified = false,
            IsRevoked = false,
            DeviceId = deviceId,
            CreatedFromIp = ip
        };

        return (plain, entity);
    }
}
