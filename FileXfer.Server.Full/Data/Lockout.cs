// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Data/Lockout.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de persistencia interna FileXferDb: representa entidades propias del servidor, no necesariamente tablas BuDi.
// Interacción: Entity Framework usa estas clases para autenticación, auditoría, nonces, refresh tokens y registro interno de cargas.
// Regla de mantenimiento: Cambios requieren migración EF y revisión de datos existentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.ComponentModel.DataAnnotations.Schema;

namespace FileXfer.Server.Data
{
    public class Lockout
    {
        public Guid Id { get; set; }

        public string Rfc { get; set; } = "";

        [Column("LockoutUntilUtc")]
        public DateTimeOffset? LockedUntilUtc { get; set; }

        public int FailCount { get; set; }

        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
