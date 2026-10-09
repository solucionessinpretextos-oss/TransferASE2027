// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/Admin/ClientUpdateInfoDto.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace FileXfer.Server.Dtos.Admin
{
    public sealed class ClientUpdateInfoDto
    {
        public string ClientId { get; set; } = string.Empty;

        public string LatestVersion { get; set; } = string.Empty;

        public string? MinSupportedVersion { get; set; }

        public string DownloadUrl { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public string SignatureBase64 { get; set; } = string.Empty;

        public string SignatureAlgorithm { get; set; } = string.Empty;

        public string? ReleaseNotes { get; set; }

        public bool IsCritical { get; set; }
    }
}
