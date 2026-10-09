// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Options/ClientUpdatesOption.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Objeto de configuración tipada: mapea secciones de appsettings/variables de entorno a clases fuertes.
// Interacción: Program.cs registra estas opciones y los servicios las reciben por inyección de dependencias.
// Regla de mantenimiento: No guardar secretos aquí; solo nombres/rutas/opciones. Los valores reales deben venir de configuración externa.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Generic;

namespace FileXfer.Server.Options
{
    public sealed class ClientUpdatesOptions
    {
        public Dictionary<string, ClientUpdateEntry> Clients { get; set; } = new();

        public sealed class ClientUpdateEntry
        {
            public string LatestVersion { get; set; } = string.Empty;
            public string MinSupportedVersion { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public string SignatureBase64 { get; set; } = string.Empty;
            public string SignatureAlgorithm { get; set; } = "RSA-PSS-SHA256";
            public string? ReleaseNotes { get; set; }
            public bool IsCritical { get; set; }
        }
    }
}
