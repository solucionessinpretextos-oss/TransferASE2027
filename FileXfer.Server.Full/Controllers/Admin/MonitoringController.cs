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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers.Admin
{
    [ApiController]
    [Route("admin/monitoring")]
    [Authorize(Policy = "PrivilegedMfa")]
    public sealed class MonitoringController : ControllerBase
    {
        private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
        private readonly ISystemHealthService _systemHealth;
        private readonly UploadSessionStore _sessions;

        public MonitoringController(
            IDbContextFactory<FileXferDbContext> dbFactory,
            ISystemHealthService systemHealth,
            UploadSessionStore sessions)
        {
            _dbFactory = dbFactory;
            _systemHealth = systemHealth;
            _sessions = sessions;
        }

        [HttpGet("summary")]
        public async Task<ActionResult<SummaryResponse>> GetSummary(CancellationToken ct)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var oneHourAgo = now.AddHours(-1);
            var staleThreshold = now.AddMinutes(-30);
            var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

            var activeSessions = _sessions.ActiveCount();
            var waitingSessions = _sessions.WaitingCount();
            var totalPendingSessions = activeSessions + waitingSessions;

            var uploads = db.Uploads.AsNoTracking();

            var completedLastHour = await uploads.CountAsync(
                u => u.Status == "Completed" && u.CompletedUtc >= oneHourAgo, ct);

            var failedLastHour = await uploads.CountAsync(
                u => u.Status == "Failed" && u.CompletedUtc >= oneHourAgo, ct);

            var completedToday = await uploads.CountAsync(
                u => u.Status == "Completed" && u.CompletedUtc >= todayStart, ct);

            var failedToday = await uploads.CountAsync(
                u => u.Status == "Failed" && u.CompletedUtc >= todayStart, ct);

            var bytesLastHour = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc >= oneHourAgo)
                .SumAsync(u => u.BytesReceived, ct);

            var bytesToday = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc >= todayStart)
                .SumAsync(u => u.BytesReceived, ct);

            var stalePendingTransfers = await uploads.CountAsync(
                u => u.Status == "Pending" && u.CompletedUtc == null && u.CreatedUtc < staleThreshold, ct);

            var distinctRfcsToday = await uploads
                .Where(u => u.CreatedUtc >= todayStart)
                .Select(u => u.Rfc)
                .Distinct()
                .CountAsync(ct);

            var averageFileSizeTodayBytes = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc >= todayStart)
                .AverageAsync(u => (double?)u.BytesReceived, ct) ?? 0;

            var lastCompletedUtc = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc != null)
                .OrderByDescending(u => u.CompletedUtc)
                .Select(u => u.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            var lastFailedUtc = await uploads
                .Where(u => u.Status == "Failed" && u.CompletedUtc != null)
                .OrderByDescending(u => u.CompletedUtc)
                .Select(u => u.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            var completedDurationsToday = await uploads
                .Where(u => u.Status == "Completed" && u.CompletedUtc != null && u.CompletedUtc >= todayStart)
                .Select(u => new { u.CreatedUtc, CompletedUtc = u.CompletedUtc!.Value })
                .ToListAsync(ct);

            var durationSecondsToday = completedDurationsToday
                .Select(x => Math.Max(0, (x.CompletedUtc - x.CreatedUtc).TotalSeconds))
                .OrderBy(x => x)
                .ToList();

            var totalTransfersToday = completedToday + failedToday;
            var transfersLastHour = completedLastHour + failedLastHour;
            var successRateTodayPercent = CalculateRate(completedToday, totalTransfersToday);
            var failureRateLastHourPercent = CalculateRate(failedLastHour, transfersLastHour);
            var averageUploadDurationTodaySeconds = durationSecondsToday.Count == 0 ? 0 : durationSecondsToday.Average();
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
                AverageThroughputMbps = avgMbps
            };

            var server = await _systemHealth.GetSnapshotAsync(ct);

            var kpis = new OperationalKpisDto
            {
                OperationalHealthScore = CalculateOperationalHealthScore(
                    server.IsHealthy,
                    failureRateLastHourPercent,
                    stalePendingTransfers,
                    totalPendingSessions),
                TotalTransfersToday = totalTransfersToday,
                SuccessRateTodayPercent = successRateTodayPercent,
                FailureRateLastHourPercent = failureRateLastHourPercent,
                AverageUploadDurationTodaySeconds = averageUploadDurationTodaySeconds,
                P95UploadDurationTodaySeconds = p95UploadDurationTodaySeconds,
                AverageFileSizeTodayBytes = averageFileSizeTodayBytes,
                StalePendingTransfers = stalePendingTransfers,
                DistinctRfcsToday = distinctRfcsToday,
                LastCompletedUtc = lastCompletedUtc,
                LastFailedUtc = lastFailedUtc
            };

            var response = new SummaryResponse
            {
                Server = server,
                Transfers = transfers,
                Kpis = kpis,
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

        private static double CalculateOperationalHealthScore(
            bool serverHealthy,
            double failureRateLastHourPercent,
            int stalePendingTransfers,
            int pendingSessions)
        {
            var score = 100.0;

            if (!serverHealthy)
                score -= 25;

            score -= Math.Min(30, failureRateLastHourPercent * 0.6);
            score -= Math.Min(20, stalePendingTransfers * 5);
            score -= Math.Min(15, pendingSessions * 2);

            return Math.Clamp(score, 0, 100);
        }
    }
}
