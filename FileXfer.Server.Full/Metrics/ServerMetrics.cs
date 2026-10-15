// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Metrics/ServerMetrics.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de métricas del servidor: agrupa indicadores operativos usados por monitoreo.
// Interacción: Los servicios de salud y controladores administrativos lo usan para mostrar KPIs en dashboard.
// Regla de mantenimiento: Distinguir métricas accionables de métricas de vanidad; cada KPI debe ayudar a operar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Diagnostics.Metrics;
namespace FileXfer.Server;
public static class ServerMetrics {
  public const string MeterName = "FileXfer.Server";
  private static readonly Meter M = new(MeterName);
  public static readonly Counter<long> UploadChunksTotal = M.CreateCounter<long>("upload_chunks_total");
  public static readonly Counter<long> UploadBytesTotal = M.CreateCounter<long>("upload_bytes_total","bytes");
  public static readonly Counter<long> UploadFilesCompletedTotal = M.CreateCounter<long>("upload_files_completed_total");
  public static readonly Counter<long> UploadFilesFailedTotal = M.CreateCounter<long>("upload_files_failed_total");
  public static readonly Histogram<double> UploadChunkLatencyMs = M.CreateHistogram<double>("upload_chunk_latency_ms","ms");
  public static readonly Histogram<double> UploadFileTotalMs = M.CreateHistogram<double>("upload_file_total_ms","ms");
  public static readonly Counter<long> UploadChunkRetryTotal = M.CreateCounter<long>("upload_chunk_retry_total");
  public static readonly Counter<long> UploadThrottleEventsTotal = M.CreateCounter<long>("upload_throttle_events_total");
  public static readonly Counter<long> AuthLoginsTotal = M.CreateCounter<long>("auth_logins_total");
  public static readonly Counter<long> AuthLoginFailuresTotal = M.CreateCounter<long>("auth_login_failures_total");
  public static readonly Counter<long> UploadResumedFilesTotal = M.CreateCounter<long>("upload_resumed_files_total");
  public static readonly Counter<long> UploadResumedBytesTotal = M.CreateCounter<long>("upload_resumed_bytes_total", "bytes");
  public static readonly Counter<long> UploadVerificationBypassedTotal = M.CreateCounter<long>("upload_verification_bypassed_total");
  public static readonly Counter<long> UploadPolicyRejectedTotal = M.CreateCounter<long>("upload_policy_rejected_total");
  public static readonly Counter<long> UploadReceiptsIssuedTotal = M.CreateCounter<long>("upload_receipts_issued_total");
  public static readonly Histogram<double> UploadValidationDurationMs = M.CreateHistogram<double>("upload_validation_duration_ms", "ms");
  public static readonly Histogram<double> UploadCatalogDurationMs = M.CreateHistogram<double>("upload_catalog_duration_ms", "ms");
  public static readonly Counter<long> FileDownloadsPreparedTotal = M.CreateCounter<long>("file_downloads_prepared_total");
  public static readonly Counter<long> FileDownloadsBlockedTotal = M.CreateCounter<long>("file_downloads_blocked_total");
  public static readonly Counter<long> FileDownloadBytesPreparedTotal = M.CreateCounter<long>("file_download_bytes_prepared_total", "bytes");
  public static readonly Histogram<double> FileDownloadVerificationDurationMs = M.CreateHistogram<double>("file_download_verification_duration_ms", "ms");
  public static readonly Counter<long> AlertDeliveryEnqueuedTotal = M.CreateCounter<long>("alert_delivery_enqueued_total");
  public static readonly Counter<long> AlertDeliveryDeliveredTotal = M.CreateCounter<long>("alert_delivery_delivered_total");
  public static readonly Counter<long> AlertDeliveryDeduplicatedTotal = M.CreateCounter<long>("alert_delivery_deduplicated_total");
  public static readonly Counter<long> AlertDeliveryDroppedTotal = M.CreateCounter<long>("alert_delivery_dropped_total");
  public static readonly Counter<long> AlertDeliveryRetryTotal = M.CreateCounter<long>("alert_delivery_retry_total");
  public static readonly Counter<long> AlertDeliveryFailedTotal = M.CreateCounter<long>("alert_delivery_failed_total");
  public static readonly Histogram<double> AlertQueueLatencyMs = M.CreateHistogram<double>("alert_queue_latency_ms", "ms");
}
