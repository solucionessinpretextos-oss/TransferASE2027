// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Options/Options.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Objeto de configuración tipada: mapea secciones de appsettings/variables de entorno a clases fuertes.
// Interacción: Program.cs registra estas opciones y los servicios las reciben por inyección de dependencias.
// Regla de mantenimiento: No guardar secretos aquí; solo nombres/rutas/opciones. Los valores reales deben venir de configuración externa.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace FileXfer.Server.Options
{
    public sealed class JwtOptions
    {
        public string Issuer { get; set; } = "FileXfer";
        public string Audience { get; set; } = "FileXfer";
        public string SigningKey { get; set; } = string.Empty;
        public int AccessTokenMinutes { get; set; } = 15;
    }

    public sealed class RateLimitOptions
    {
        public int PermitLimit { get; set; } = 10;
        public int WindowSeconds { get; set; } = 60;
        public int QueueLimit { get; set; } = 0;
        public int LockoutThreshold { get; set; } = 5;
        public int LockoutMinutes { get; set; } = 30;
    }

    public sealed class DevicePolicyOptions
    {
        public int RotationDays { get; set; } = 180;
        public int MinRsaBits { get; set; } = 2048;
        public bool BlockLoginWhenExpired { get; set; } = false;
    }

    public sealed class SatOptions
    {
        public bool AllowDevelopmentBypass { get; set; } = false;

        public string? DevBypassToken { get; set; }

        public bool RequireOcsp { get; set; } = true;

        public int AcceptClockSkewMinutes { get; set; } = 10;

        public int OnlineTimeoutMs { get; set; } = 4000;

        public bool FailOnUnknownRevocation { get; set; } = true;

        public string[]? PreferredOcspOrCrlEndpoints { get; set; }

        public string[] TrustedRootCertificatePaths { get; set; } = Array.Empty<string>();

        public string[] IntermediateCertificatePaths { get; set; } = Array.Empty<string>();

        public string[] AllowedRevocationHosts { get; set; } = Array.Empty<string>();
    }

    public sealed class DebugFeaturesOptions
    {
        public bool EnableDebugUploadEndpoints { get; set; } = false;
        public string[] AllowedEnvironments { get; set; } = new[] { "Development", "Staging" };
        public string[] AllowedRoles { get; set; } = new[] { "admin", "ops" };
    }

    public sealed class AlertOptions
    {
        public bool Enabled { get; set; } = true;

        public string? EmailTo { get; set; }

        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 25;
        public bool SmtpUseTls { get; set; } = true;
        public string? SmtpUser { get; set; }
        public string? SmtpPassword { get; set; }
        public string? EmailFrom { get; set; }
        public int SmtpTimeoutSeconds { get; set; } = 15;

        public string? WebhookUrl { get; set; }
        public int WebhookTimeoutSeconds { get; set; } = 10;

        public int QueueCapacity { get; set; } = 256;
        public int WorkerCount { get; set; } = 2;
        public int MaxDeliveryAttempts { get; set; } = 3;
        public int RetryInitialDelayMilliseconds { get; set; } = 1000;
        public int DeduplicationWindowSeconds { get; set; } = 60;
        public int ShutdownDrainSeconds { get; set; } = 15;
        public int PersistenceTimeoutSeconds { get; set; } = 5;
        public int PollIntervalMilliseconds { get; set; } = 500;
        public int LeaseSeconds { get; set; } = 180;
        public int BatchSize { get; set; } = 32;
        public int DeliveredRetentionDays { get; set; } = 30;
        public int DeadLetterRetentionDays { get; set; } = 90;
        public int CleanupIntervalHours { get; set; } = 6;
    }
}
