// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Data/RfcProvisioningRequest.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de persistencia interna FileXferDb: representa entidades propias del servidor, no necesariamente tablas BuDi.
// Interacción: Entity Framework usa estas clases para autenticación, auditoría, nonces, refresh tokens y registro interno de cargas.
// Regla de mantenimiento: Cambios requieren migración EF y revisión de datos existentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.ComponentModel.DataAnnotations;

namespace FileXfer.Server.Data;

public sealed class RfcProvisioningRequest
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(13)]
    public string Rfc { get; set; } = "";

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    [MaxLength(256)]
    public string? ContactEmail { get; set; }

    [MaxLength(1024)]
    public string? Reason { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "Pending"; // Pending | Approved | Rejected

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(64)]
    public string? CreatedBy { get; set; }

    public DateTimeOffset? ReviewedUtc { get; set; }
    [MaxLength(64)]
    public string? ReviewedBy { get; set; }

    [MaxLength(1024)]
    public string? ReviewNotes { get; set; }
}
