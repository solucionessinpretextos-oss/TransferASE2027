// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Data/UploadEntry.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de persistencia interna FileXferDb: representa entidades propias del servidor, no necesariamente tablas BuDi.
// Interacción: Entity Framework usa estas clases para autenticación, auditoría, nonces, refresh tokens y registro interno de cargas.
// Regla de mantenimiento: Cambios requieren migración EF y revisión de datos existentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.ComponentModel.DataAnnotations;

namespace FileXfer.Server.Data
{
    public class UploadEntry
    {
        public Guid Id { get; set; }

        [Required, MaxLength(13)]
        public string Rfc { get; set; } = string.Empty;

        [Required, MaxLength(32)]
        public string FileUuid { get; set; } = string.Empty;

        [MaxLength(255)]
        public string? OriginalFileName { get; set; }

        [MaxLength(450)]
        public string? OriginalSourcePath { get; set; }

        [Required, MaxLength(450)]
        public string StoredRelativePath { get; set; } = string.Empty;

        public long BytesReceived { get; set; }

        [MaxLength(64)]
        public string? Sha256Server { get; set; }

        [MaxLength(64)]
        public string? Sha256Client { get; set; }

        [Required, MaxLength(32)]
        public string Status { get; set; } = "Pending";

        [MaxLength(1024)]
        public string? LastError { get; set; }

        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }

        [MaxLength(64)]
        public string? DeviceId { get; set; }

        public int? IdEnte { get; set; }

        public int? IdAuditoria { get; set; }

        public int? IdActo { get; set; }

        public int? IdOficio { get; set; }

        public int? IdRequerimiento { get; set; }

        public int? IdResultado { get; set; }

        public int? Periodo { get; set; }
    }
}
