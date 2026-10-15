// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/Admin/AdminMonitoringDtos.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;

namespace FileXfer.Server.Dtos.Admin
{
    public sealed class ServerHealthDto
    {
        public bool IsHealthy { get; set; }
        public string? Message { get; set; }
        public double CpuUsagePercent { get; set; }
        public double MemoryUsagePercent { get; set; }
        public double DiskUsagePercent { get; set; }
        public bool DatabaseOk { get; set; }
        public bool StorageOk { get; set; }
        public bool MessageBusOk { get; set; }
        public bool AlertingOk { get; set; }
        public bool AlertingEnabled { get; set; }
        public int AlertQueuePending { get; set; }
        public int AlertQueueInFlight { get; set; }
        public int AlertDeadLetter { get; set; }
        public int AlertQueueCapacity { get; set; }
        public long AlertEnqueuedTotal { get; set; }
        public long AlertDeliveredTotal { get; set; }
        public long AlertDeduplicatedTotal { get; set; }
        public long AlertDroppedTotal { get; set; }
        public long AlertRetryTotal { get; set; }
        public long AlertTerminalFailureTotal { get; set; }
        public DateTimeOffset? AlertLastDeliveredUtc { get; set; }
        public DateTimeOffset? AlertLastFailureUtc { get; set; }
    }

    public sealed class TransferSummaryDto
    {
        public int ActiveTransfers { get; set; }
        public int WaitingTransfers { get; set; }
        public int TotalPendingTransfers { get; set; }
        public int CompletedLastHour { get; set; }
        public int FailedLastHour { get; set; }
        public int CompletedToday { get; set; }
        public int FailedToday { get; set; }
        public long BytesTransferredLastHour { get; set; }
        public long TotalBytesTransferredToday { get; set; }
        public double AverageThroughputMbps { get; set; }
        public int PausedResumableTransfers { get; set; }
        public int ResumedTransfersToday { get; set; }
        public long ResumedBytesToday { get; set; }
        public int VerificationBypassedToday { get; set; }
        public int ReceiptsIssuedToday { get; set; }
    }

    public sealed class OperationalKpisDto
    {
        public double OperationalHealthScore { get; set; }
        public int TotalTransfersToday { get; set; }
        public double SuccessRateTodayPercent { get; set; }
        public double FailureRateLastHourPercent { get; set; }
        public double AverageUploadDurationTodaySeconds { get; set; }
        public double P95UploadDurationTodaySeconds { get; set; }
        public double AverageFileSizeTodayBytes { get; set; }
        public int StalePendingTransfers { get; set; }
        public int DistinctRfcsToday { get; set; }
        public DateTimeOffset? LastCompletedUtc { get; set; }
        public DateTimeOffset? LastFailedUtc { get; set; }
    }

    public sealed class SummaryResponse
    {
        public ServerHealthDto Server { get; set; } = default!;
        public TransferSummaryDto Transfers { get; set; } = default!;
        public OperationalKpisDto Kpis { get; set; } = default!;
        public IReadOnlyList<RecentTransferDto> RecentTransfers { get; set; } = Array.Empty<RecentTransferDto>();
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class RecentTransferDto
    {
        public string FileName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long ResumedBytes { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string? PolicyExtension { get; set; }
        public int? PolicyVersion { get; set; }
        public bool VerificationRequired { get; set; }
        public bool VerificationPerformed { get; set; }
        public string? TraceId { get; set; }
        public Guid? ReceiptId { get; set; }
    }
}
