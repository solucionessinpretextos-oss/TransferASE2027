// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Data/Entities.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de persistencia interna FileXferDb: representa entidades propias del servidor, no necesariamente tablas BuDi.
// Interacción: Entity Framework usa estas clases para autenticación, auditoría, nonces, refresh tokens y registro interno de cargas.
// Regla de mantenimiento: Cambios requieren migración EF y revisión de datos existentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.ComponentModel.DataAnnotations;

namespace FileXfer.Server.Data;

public sealed class RfcUser
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(13)]
    public string Rfc { get; set; } = "";

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    public string[] Roles { get; set; } = Array.Empty<string>();

    public bool MfaRequired { get; set; } = false;

    public bool IsActive { get; set; } = true;

    public int SecurityVersion { get; set; } = 1;

    [MaxLength(1024)]
    public string? Notes { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(64)]
    public string? CreatedBy { get; set; }

    public DateTimeOffset? LastModifiedUtc { get; set; }

    [MaxLength(64)]
    public string? LastModifiedBy { get; set; }
}

public sealed class Device
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(64)]
    public string DeviceId { get; set; } = "";

    [Required, MaxLength(13)]
    public string Rfc { get; set; } = "";

    [Required, MaxLength(8192)]
    public string PublicKeyPem { get; set; } = "";

    [MaxLength(256)]
    public string? FriendlyName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool Revoked { get; set; } = false;

    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class MfaSecret
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(13)]
    public string Rfc { get; set; } = "";

    [Required, MaxLength(256)]
    public string SecretBase32 { get; set; } = "";

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Nonce
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string ValueBase64 { get; set; } = "";

    public DateTimeOffset ExpiresUtc { get; set; }

    [MaxLength(64)]
    public string? DeviceId { get; set; }

    [MaxLength(13)]
    public string? Rfc { get; set; }
}

public sealed class AuditLog
{
    [Key]
    public Guid Id { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    [MaxLength(13)]
    public string? ActorRfc { get; set; }

    [MaxLength(128)]
    public string? Action { get; set; }

    [MaxLength(64)]
    public string? Ip { get; set; }

    [MaxLength(2048)]
    public string? Details { get; set; }
}
public sealed class RefreshToken
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(13)]
    public string Rfc { get; set; } = "";

    [Required, MaxLength(256)]
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresUtc { get; set; }

    public Guid FamilyId { get; set; }

    public Guid? ParentTokenId { get; set; }

    public int SecurityVersion { get; set; }

    public bool MfaVerified { get; set; }

    public bool IsRevoked { get; set; }

    [MaxLength(64)]
    public string? DeviceId { get; set; }

    [MaxLength(64)]
    public string? CreatedFromIp { get; set; }
}
