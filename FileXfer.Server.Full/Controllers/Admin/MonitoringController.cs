// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Controllers/Admin/MonitoringController.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Controlador de monitoreo administrativo.
// Interacción: Expone resumen de salud, KPIs, transferencias y señales para dashboard React.
// Regla de mantenimiento: No colocar SQL directo aquí si la consulta pertenece a BuDi; mantenerlo delgado para que la lógica se pruebe y mantenga en servicios.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Data;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Services;
using FileXfer.Server.Uploading;
using FileXfer.Server.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers.Admin
{
    [ApiController]
    [Route("admin/monitoring")]
    [Authorize(Policy = "PrivilegedEfirma")]
    public sealed class MonitoringController : ControllerBase
    {
        private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
        private readonly ISystemHealthService _systemHealth;
        private readonly UploadSessionStore _sessions;
        private readonly StorageOptions _storage;

        public MonitoringController(
            IDbContextFactory<FileXferDbContext> dbFactory,
            ISystemHealthService systemHealth,
            UploadSessionStore sessions,
            StorageOptions storage)
        {
            _dbFactory = dbFactory;
            _systemHealth = systemHealth;
            _sessions = sessions;
            _storage = storage;
        }

        [HttpGet("summary")]
        public async Task<ActionResult<SummaryResponse>> GetSummary(CancellationToken ct)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var statisticsStartedUtc = await MonitoringStatisticsService.GetStartedUtcAsync(db, ct);
            var hourWindowStart = now.AddHours(-1);
            var oneHourAgo = statisticsStartedUtc > hourWindowStart ? statisticsStartedUtc.Value : hourWindowStart;
            var staleThreshold = now.AddMinutes(-30);
            var dashboardTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mazatlan");
            var localToday = TimeZoneInfo.ConvertTime(now, dashboardTimeZone).Date;
            var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localToday, dashboardTimeZone));
            var todayStart = statisticsStartedUtc > dayStart ? statisticsStartedUtc.Value : dayStart;

            // Las sesiones admitidas incluyen a las que esperan su primer bloque: no contarlas dos veces.
            var liveSessions = _sessions.Sessions.Values.ToArray();
            var activeUuids = liveSessions.Select(session => session.FileUuid).ToArray();
            var waitingSessions = liveSessions.Count(session => session.BytesReceived == 0);
            var activeSessions = liveSessions.Length - waitingSessions;
            var totalPendingSessions = liveSessions.Length;

            var uploads = db.Uploads.AsNoTracking();
            var pending = uploads.Where(u => (u.Status == "Pending" || u.Status == "Paused") && u.CompletedUtc == null);
            // EF.Constant evita OPENJSON, no disponible en el SQL Server 2014 instalado.
            var inactivePending = pending.Where(u => !EF.Constant(activeUuids).Contains(u.FileUuid));
            var fallbackExpiryThreshold = now.AddHours(-_storage.ResumeRetentionHours);
            var nearExpiryThreshold = now.AddHours(24);
            var fallbackNearThreshold = nearExpiryThreshold.AddHours(-_storage.ResumeRetentionHours);
            var expiredPending = inactivePending.Where(u =>
                (u.ResumeExpiresUtc != null && u.ResumeExpiresUtc <= now) ||
                (u.ResumeExpiresUtc == null &&
                    (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) <= fallbackExpiryThreshold));
            var nearExpiry = inactivePending.Where(u =>
                (u.ResumeExpiresUtc > now && u.ResumeExpiresUtc <= nearExpiryThreshold) ||
                (u.ResumeExpiresUtc == null &&
                    (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) > fallbackExpiryThreshold &&
                    (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) <= fallbackNearThreshold));
            var expiredPendingTransfers = await expiredPending.CountAsync(ct);
            var oldestPendingUtc = await inactivePending.OrderBy(u => u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc)
                .Select(u => (DateTimeOffset?)(u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc)).FirstOrDefaultAsync(ct);

            var completedLastHour = await uploads.CountAsync(
                u => u.Status == "Completed" && u.CompletedUtc >= oneHourAgo, ct);

            var failedLastHour = await uploads.CountAsync(
                u => (u.Status == "Failed" ||
                      u.Status == "CatalogFailed" ||
                      u.Status == "Rejected") &&
                     u.CompletedUtc >= oneHourAgo, ct);

            var completedTodayDetails = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc != null && u.CompletedUtc >= todayStart)
                .Select(u => new
                {
                    u.CreatedUtc,
                    CompletedUtc = u.CompletedUtc!.Value,
                    u.BytesReceived,
                    u.ReceiptId,
                    u.VerificationRequired,
                    u.VerificationPerformed
                })
                .ToListAsync(ct);

            var completedToday = completedTodayDetails.Count;

            var failedToday = await uploads.CountAsync(
                u => (u.Status == "Failed" ||
                      u.Status == "CatalogFailed" ||
                      u.Status == "Rejected") &&
                     u.CompletedUtc >= todayStart, ct);

            var catalogFailuresToday = await uploads.CountAsync(
                u => u.Status == "CatalogFailed" && u.CompletedUtc >= todayStart, ct);

            var bytesLastHour = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc >= oneHourAgo)
                .SumAsync(u => u.BytesReceived, ct);

            var bytesToday = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc >= todayStart)
                .SumAsync(u => u.BytesReceived, ct);

            var stalePendingTransfers = await inactivePending.CountAsync(
                u => (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) < staleThreshold &&
                     ((u.ResumeExpiresUtc != null && u.ResumeExpiresUtc > now) ||
                      (u.ResumeExpiresUtc == null &&
                        (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) > fallbackExpiryThreshold)), ct);

            var distinctRfcsToday = await uploads
                .Where(u => u.CreatedUtc >= todayStart || u.CompletedUtc >= todayStart)
                .Select(u => u.Rfc)
                .Distinct()
                .CountAsync(ct);

            var pausedResumableTransfers = await uploads.CountAsync(
                u => u.Status == "Paused" && u.ResumeExpiresUtc > now, ct);

            var resumedTransfersToday = await uploads.CountAsync(
                u => u.CreatedUtc >= todayStart && u.ResumedBytes > 0, ct);

            var resumedBytesToday = await uploads
                .Where(u => u.CreatedUtc >= todayStart && u.ResumedBytes > 0)
                .SumAsync(u => u.ResumedBytes, ct);

            var verificationBypassedToday = await uploads.CountAsync(
                u => u.CompletedUtc >= todayStart &&
                     u.VerificationRequired == false &&
                     u.VerificationOutcome == "BypassedByPolicy", ct);

            var receiptsIssuedToday = await db.UploadReceipts.AsNoTracking()
                .CountAsync(receipt => receipt.IssuedUtc >= todayStart, ct);

            var orderedFileSizesToday = completedTodayDetails
                .Select(u => (double)u.BytesReceived)
                .OrderBy(value => value)
                .ToList();

            var averageFileSizeTodayBytes = orderedFileSizesToday.Count == 0
                ? 0
                : orderedFileSizesToday.Average();

            var p95FileSizeTodayBytes = PercentileNearestRank(orderedFileSizesToday, 95);

            var completedWithReceiptToday = completedTodayDetails.Count(u => u.ReceiptId != null);
            var completedWithoutReceiptToday = completedToday - completedWithReceiptToday;
            var verificationRequiredToday = completedTodayDetails.Count(u => u.VerificationRequired);
            var verificationPerformedToday = completedTodayDetails.Count(u => u.VerificationRequired && u.VerificationPerformed);
            var receiptCoverageTodayPercent = completedToday == 0
                ? 100
                : CalculateRate(completedWithReceiptToday, completedToday);
            var verificationCoverageTodayPercent = verificationRequiredToday == 0
                ? 100
                : CalculateRate(verificationPerformedToday, verificationRequiredToday);
            var resumeReuseTodayPercent = bytesToday <= 0
                ? 0
                : Math.Clamp(resumedBytesToday * 100.0 / bytesToday, 0, 100);

            var lastCompletedUtc = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc != null &&
                            (statisticsStartedUtc == null || u.CompletedUtc >= statisticsStartedUtc))
                .OrderByDescending(u => u.CompletedUtc)
                .Select(u => u.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            var lastFailedUtc = await uploads
                .Where(u => (u.Status == "Failed" ||
                             u.Status == "CatalogFailed" ||
                             u.Status == "Rejected") &&
                            u.CompletedUtc != null &&
                            (statisticsStartedUtc == null || u.CompletedUtc >= statisticsStartedUtc))
                .OrderByDescending(u => u.CompletedUtc)
                .Select(u => u.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            var durationSecondsToday = completedTodayDetails
                .Select(x => Math.Max(0, (x.CompletedUtc - x.CreatedUtc).TotalSeconds))
                .OrderBy(x => x)
                .ToList();

            var totalTransfersToday = completedToday + failedToday;
            var transfersLastHour = completedLastHour + failedLastHour;
            var successRateTodayPercent = CalculateRate(completedToday, totalTransfersToday);
            var failureRateTodayPercent = CalculateRate(failedToday, totalTransfersToday);
            var failureRateLastHourPercent = CalculateRate(failedLastHour, transfersLastHour);
            var averageUploadDurationTodaySeconds = durationSecondsToday.Count == 0 ? 0 : durationSecondsToday.Average();
            var medianUploadDurationTodaySeconds = PercentileNearestRank(durationSecondsToday, 50);
            var p95UploadDurationTodaySeconds = PercentileNearestRank(durationSecondsToday, 95);

            double avgMbps = 0;
            var seconds = (now - oneHourAgo).TotalSeconds;

            if (bytesLastHour > 0 && seconds > 0)
                avgMbps = (bytesLastHour * 8.0 / 1_000_000.0) / seconds;

            var transfers = new TransferSummaryDto
            {
                ActiveTransfers = activeSessions,
                WaitingTransfers = waitingSessions,
                TotalPendingTransfers = totalPendingSessions,
                CompletedLastHour = completedLastHour,
                FailedLastHour = failedLastHour,
                CompletedToday = completedToday,
                FailedToday = failedToday,
                BytesTransferredLastHour = bytesLastHour,
                TotalBytesTransferredToday = bytesToday,
                AverageThroughputMbps = avgMbps,
                PausedResumableTransfers = pausedResumableTransfers,
                ResumedTransfersToday = resumedTransfersToday,
                ResumedBytesToday = resumedBytesToday,
                VerificationBypassedToday = verificationBypassedToday,
                ReceiptsIssuedToday = receiptsIssuedToday
            };

            var server = await _systemHealth.GetSnapshotAsync(ct);
            // La salud usa la hora real, incluso después de reiniciar las estadísticas visibles.
            var healthCompleted = await uploads.CountAsync(u => u.Status == "Completed" && u.CompletedUtc >= hourWindowStart, ct);
            var healthFailed = await uploads.CountAsync(u =>
                (u.Status == "Failed" || u.Status == "CatalogFailed" || u.Status == "Rejected") &&
                u.CompletedUtc >= hourWindowStart, ct);
            var healthBreakdown = OperationalHealthCalculator.Explain(
                server, CalculateRate(healthFailed, healthCompleted + healthFailed), stalePendingTransfers, expiredPendingTransfers);

            var kpis = new OperationalKpisDto
            {
                OperationalHealthScore = OperationalHealthCalculator.Score(healthBreakdown),
                TotalTransfersToday = totalTransfersToday,
                SuccessRateTodayPercent = successRateTodayPercent,
                FailureRateTodayPercent = failureRateTodayPercent,
                FailureRateLastHourPercent = failureRateLastHourPercent,
                AverageUploadDurationTodaySeconds = averageUploadDurationTodaySeconds,
                MedianUploadDurationTodaySeconds = medianUploadDurationTodaySeconds,
                P95UploadDurationTodaySeconds = p95UploadDurationTodaySeconds,
                AverageFileSizeTodayBytes = averageFileSizeTodayBytes,
                P95FileSizeTodayBytes = p95FileSizeTodayBytes,
                ReceiptCoverageTodayPercent = receiptCoverageTodayPercent,
                VerificationCoverageTodayPercent = verificationCoverageTodayPercent,
                ResumeReuseTodayPercent = resumeReuseTodayPercent,
                CatalogFailuresToday = catalogFailuresToday,
                CompletedWithoutReceiptToday = completedWithoutReceiptToday,
                StalePendingTransfers = stalePendingTransfers,
                DistinctRfcsToday = distinctRfcsToday,
                PendingTransfers = await pending.CountAsync(ct),
                ExpiredPendingTransfers = expiredPendingTransfers,
                ExpiringNext24Hours = await nearExpiry.CountAsync(ct),
                PendingBytes = await pending.SumAsync(u => u.BytesReceived, ct),
                OldestPendingAgeHours = oldestPendingUtc == null ? 0 : Math.Max(0, (now - oldestPendingUtc.Value).TotalHours),
                NearExpiryBytes = await nearExpiry.SumAsync(u => u.BytesReceived, ct),
                AdministrativelyCancelledToday = await uploads.CountAsync(u => u.Status == "Cancelled" &&
                    u.CompletedUtc >= todayStart && u.LastError != null && u.LastError.StartsWith("Eliminación manual"), ct),
                ExpiredToday = await uploads.CountAsync(u => u.Status == "Expired" && u.CompletedUtc >= todayStart, ct),
                LastCompletedUtc = lastCompletedUtc,
                LastFailedUtc = lastFailedUtc
            };

            var response = new SummaryResponse
            {
                Server = server,
                Transfers = transfers,
                Kpis = kpis,
                StatisticsStartedUtc = statisticsStartedUtc,
                HealthBreakdown = healthBreakdown,
                RecentTransfers = await uploads
                    .Where(u => statisticsStartedUtc == null || u.CreatedUtc >= statisticsStartedUtc ||
                                u.CompletedUtc >= statisticsStartedUtc || u.Status == "Pending" || u.Status == "Paused")
                    .OrderByDescending(u => u.CreatedUtc)
                    .Take(20)
                    .Select(u => new RecentTransferDto
                    {
                        UploadId = u.Id,
                        Rfc = u.Rfc,
                        FileName = u.OriginalFileName ?? string.Empty,
                        Status = u.Status,
                        BytesReceived = u.BytesReceived,
                        ResumedBytes = u.ResumedBytes,
                        CreatedUtc = u.CreatedUtc,
                        CompletedUtc = u.CompletedUtc,
                        PolicyExtension = u.PolicyExtension,
                        PolicyVersion = u.PolicyVersion,
                        VerificationRequired = u.VerificationRequired,
                        VerificationPerformed = u.VerificationPerformed,
                        VerificationOutcome = u.VerificationOutcome,
                        LastError = u.LastError,
                        TraceId = u.TraceId,
                        ReceiptId = u.ReceiptId
                    })
                    .ToListAsync(ct),
                TimestampUtc = DateTime.UtcNow
            };

            return Ok(response);
        }

        private static double CalculateRate(int numerator, int denominator)
        {
            return denominator <= 0 ? 0 : numerator * 100.0 / denominator;
        }

        private static double PercentileNearestRank(IReadOnlyList<double> orderedValues, int percentile)
        {
            if (orderedValues.Count == 0)
                return 0;

            var rank = (int)Math.Ceiling(percentile / 100.0 * orderedValues.Count);
            var index = Math.Clamp(rank - 1, 0, orderedValues.Count - 1);
            return orderedValues[index];
        }

    }
}
