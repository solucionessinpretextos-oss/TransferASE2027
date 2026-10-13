// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Options/StorageOptions.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Objeto de configuración tipada: mapea secciones de appsettings/variables de entorno a clases fuertes.
// Interacción: Program.cs registra estas opciones y los servicios las reciben por inyección de dependencias.
// Regla de mantenimiento: No guardar secretos aquí; solo nombres/rutas/opciones. Los valores reales deben venir de configuración externa.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace FileXfer.Server.Options
{
    public class StorageOptions
    {
        public string BasePath { get; set; } = string.Empty;

        public long MaxUploadBytes { get; set; } = 1024L * 1024L * 1024L;

        public int MaxChunkBytes { get; set; } = 2 * 1024 * 1024;

        public int MaxConcurrentUploadsTotal { get; set; } = 200;

        public int MaxConcurrentUploadsPerRfc { get; set; } = 5;

        public int FirstChunkTimeoutSeconds { get; set; } = 30;

        public int IdleChunkTimeoutSeconds { get; set; } = 120;

        public int MaxUploadDurationMinutes { get; set; } = 60;

        public int ResumeRetentionHours { get; set; } = 24;
    }
}
