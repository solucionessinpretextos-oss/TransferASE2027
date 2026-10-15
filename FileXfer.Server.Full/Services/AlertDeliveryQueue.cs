using System.Security.Cryptography;
using System.Text;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public sealed record AlertDeliverySnapshot(
    bool Enabled,
    bool IsHealthy,
    int Pending,
    int InFlight,
    int DeadLetter,
    int Capacity,
    long EnqueuedTotal,
    long DeliveredTotal,
    long DeduplicatedTotal,
    long DroppedTotal,
    long RetryTotal,
    long TerminalFailureTotal,
    DateTimeOffset? LastDeliveredUtc,
    DateTimeOffset? LastFailureUtc,
    DateTimeOffset? LastDroppedUtc);

public interface IAlertDeliveryState
{
    AlertDeliverySnapshot GetSnapshot();
}

/// <summary>
/// Outbox SQL de alertas. El productor confirma únicamente después de persistir
/// las entregas; los workers realizan SMTP/webhook fuera de la petición original.
/// Las entregas son al-menos-una-vez y se recuperan tras reinicios mediante leases.
/// </summary>
public sealed class AlertDeliveryQueue : BackgroundService, IAlertSink, IAlertDeliveryState
{
    private static readonly TimeSpan HealthFailureWindow = TimeSpan.FromMinutes(5);
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly LogAlertSink _logSink;
    private readonly IReadOnlyDictionary<string, IAlertTransport> _transports;
    private readonly IAlertTransport[] _configuredTransports;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertDeliveryQueue> _log;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private long _pending;
    private long _inFlight;
    private long _deadLetter;
    private long _enqueuedTotal;
    private long _deliveredTotal;
    private long _deduplicatedTotal;
    private long _droppedTotal;
    private long _retryTotal;
    private long _terminalFailureTotal;
    private long _lastDeliveredUnixMilliseconds;
    private long _lastFailureUnixMilliseconds;
    private long _lastDroppedUnixMilliseconds;
    private long _nextCleanupUnixMilliseconds;
    private long _lastDatabaseWarningUnixMilliseconds;
    private int _accepting = 1;

    public AlertDeliveryQueue(
        IDbContextFactory<FileXferDbContext> dbFactory,
        LogAlertSink logSink,
        IEnumerable<IAlertTransport> transports,
        IOptions<AlertOptions> options,
        ILogger<AlertDeliveryQueue> log)
    {
        _dbFactory = dbFactory;
        _logSink = logSink;
        _options = options.Value;
        _log = log;
        _transports = transports
            .GroupBy(transport => transport.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        _configuredTransports = _transports.Values.Where(transport => transport.IsConfigured).ToArray();
    }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        title = LogAlertSink.NormalizeTitle(title);
        message = LogAlertSink.Limit(message, 16_384);
        await _logSink.NotifySecurityEventAsync(title, message, CancellationToken.None);

        if (!_options.Enabled || Volatile.Read(ref _accepting) == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var key = ComputeDeduplicationKey(title, message);
        var bucket = _options.DeduplicationWindowSeconds == 0
            ? BitConverter.ToInt64(eventId.ToByteArray(), 0)
            : now.ToUnixTimeSeconds() / _options.DeduplicationWindowSeconds;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.PersistenceTimeoutSeconds));
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(timeout.Token);

            if (_options.DeduplicationWindowSeconds > 0)
            {
                var since = now.AddSeconds(-_options.DeduplicationWindowSeconds);
                var duplicate = await db.AlertOutboxDeliveries
                    .AsNoTracking()
                    .AnyAsync(row => row.DeduplicationKey == key && row.CreatedUtc >= since, timeout.Token);
                if (duplicate)
                {
                    RecordDeduplicated();
                    return;
                }
            }

            var rows = _configuredTransports.Select(transport => new AlertOutboxDelivery
            {
                EventId = eventId,
                Transport = LogAlertSink.Limit(transport.Name, 32),
                DeduplicationKey = key,
                DeduplicationBucket = bucket,
                Title = title,
                Message = message,
                OccurredUtc = now,
                Status = AlertOutboxStatuses.Pending,
                NextAttemptUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now
            }).ToArray();

            if (rows.Length == 0)
                return;

            db.AlertOutboxDeliveries.AddRange(rows);
            await db.SaveChangesAsync(timeout.Token);

            Interlocked.Add(ref _pending, rows.Length);
            Interlocked.Add(ref _enqueuedTotal, rows.Length);
            ServerMetrics.AlertDeliveryEnqueuedTotal.Add(rows.Length);
            WakeWorkers();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            RecordDeduplicated();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedTotal);
            Interlocked.Exchange(ref _lastDroppedUnixMilliseconds, now.ToUnixTimeMilliseconds());
            ServerMetrics.AlertDeliveryDroppedTotal.Add(1);
            _log.LogCritical(
                "No se pudo persistir la alerta {AlertEventId} en el outbox SQL; ErrorType={ErrorType}",
                eventId,
                ex.GetType().Name);
        }
    }

    public AlertDeliverySnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var lastDelivered = ReadTimestamp(ref _lastDeliveredUnixMilliseconds);
        var lastFailure = ReadTimestamp(ref _lastFailureUnixMilliseconds);
        var lastDropped = ReadTimestamp(ref _lastDroppedUnixMilliseconds);
        var pending = ToInt32Saturated(Interlocked.Read(ref _pending));
        var inFlight = ToInt32Saturated(Interlocked.Read(ref _inFlight));
        var deadLetter = ToInt32Saturated(Interlocked.Read(ref _deadLetter));
        var recentFailure = lastFailure is not null &&
                            now - lastFailure.Value <= HealthFailureWindow &&
                            (lastDelivered is null || lastDelivered < lastFailure);
        var recentDrop = lastDropped is not null && now - lastDropped.Value <= HealthFailureWindow;
        var saturated = pending + inFlight >= Math.Max(1, (int)(_options.QueueCapacity * 0.9));

        return new AlertDeliverySnapshot(
            _options.Enabled,
            !_options.Enabled || (!recentFailure && !recentDrop && !saturated && deadLetter == 0),
            pending,
            inFlight,
            deadLetter,
            _options.QueueCapacity,
            Interlocked.Read(ref _enqueuedTotal),
            Interlocked.Read(ref _deliveredTotal),
            Interlocked.Read(ref _deduplicatedTotal),
            Interlocked.Read(ref _droppedTotal),
            Interlocked.Read(ref _retryTotal),
            Interlocked.Read(ref _terminalFailureTotal),
            lastDelivered,
            lastFailure,
            lastDropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        await RefreshStateAsync(stoppingToken);
        await CleanupAsync(stoppingToken);

        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(workerId => ConsumeAsync(workerId, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        WakeWorkers();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _wakeSignal.Dispose();
        base.Dispose();
    }

    private async Task ConsumeAsync(int workerId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var processed = 0;
                    while (processed < _options.BatchSize && !ct.IsCancellationRequested)
                    {
                        var delivery = await TryClaimNextAsync(ct);
                        if (delivery is null)
                            break;

                        processed++;
                        await DeliverClaimedAsync(delivery, ct);
                    }

                    if (workerId == 0)
                    {
                        await RefreshStateAsync(ct);
                        await CleanupIfDueAsync(ct);
                    }

                    if (processed == 0)
                        await WaitForWorkAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(
                        ref _lastFailureUnixMilliseconds,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    LogDatabaseWarning(ex, "El worker del outbox SQL tuvo un fallo recuperable");
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds),
                        ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El trabajo no confirmado permanece en SQL y el lease permitirá recuperarlo.
        }
    }

    private async Task<AlertOutboxDelivery?> TryClaimNextAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AlertOutboxDeliveries
                .Where(item =>
                    (item.Status == AlertOutboxStatuses.Pending && item.NextAttemptUtc <= now) ||
                    (item.Status == AlertOutboxStatuses.Processing && item.LeaseExpiresUtc <= now))
                .OrderBy(item => item.NextAttemptUtc)
                .ThenBy(item => item.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (row is null)
                return null;

            var leaseId = Guid.NewGuid();
            row.Status = AlertOutboxStatuses.Processing;
            row.LeaseId = leaseId;
            row.LeaseExpiresUtc = now.AddSeconds(_options.LeaseSeconds);
            row.UpdatedUtc = now;
            try
            {
                await db.SaveChangesAsync(ct);
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _inFlight);
                ServerMetrics.AlertQueueLatencyMs.Record(
                    Math.Max(0, (now - row.OccurredUtc).TotalMilliseconds));
                return row;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Otro worker o instancia obtuvo el lease primero.
            }
        }

        return null;
    }

    private async Task DeliverClaimedAsync(AlertOutboxDelivery delivery, CancellationToken ct)
    {
        try
        {
            AlertTransportResult result;
            if (!_transports.TryGetValue(delivery.Transport, out var transport) || !transport.IsConfigured)
            {
                result = AlertTransportResult.Failed("transport_not_configured");
            }
            else
            {
                try
                {
                    result = await transport.SendAsync(
                        new SecurityAlertEnvelope(
                            delivery.EventId,
                            delivery.Title,
                            delivery.Message,
                            delivery.OccurredUtc),
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await ReleaseLeaseAsync(delivery, CancellationToken.None);
                    return;
                }
                catch (Exception ex)
                {
                    // Nunca registrar el mensaje completo de una excepción HTTP: puede contener
                    // una URL con credenciales o tokens en el query string.
                    result = AlertTransportResult.Failed(ex.GetType().Name);
                }
            }

            if (result.Success)
                await MarkDeliveredAsync(delivery, ct);
            else
                await MarkFailedAsync(delivery, result.FailureCode, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task MarkDeliveredAsync(AlertOutboxDelivery delivery, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await FindOwnedLeaseAsync(db, delivery, ct);
        if (row is null)
            return;

        row.Status = AlertOutboxStatuses.Delivered;
        row.DeliveredUtc = now;
        row.LeaseId = null;
        row.LeaseExpiresUtc = null;
        row.LastFailureCode = null;
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);

        Interlocked.Increment(ref _deliveredTotal);
        Interlocked.Exchange(ref _lastDeliveredUnixMilliseconds, now.ToUnixTimeMilliseconds());
        ServerMetrics.AlertDeliveryDeliveredTotal.Add(1);
        _log.LogInformation(
            "Alerta {AlertEventId} entregada por {AlertTransport} en intento {Attempt}",
            delivery.EventId,
            delivery.Transport,
            delivery.AttemptCount + 1);
    }

    private async Task MarkFailedAsync(
        AlertOutboxDelivery delivery,
        string? failureCode,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await FindOwnedLeaseAsync(db, delivery, ct);
        if (row is null)
            return;

        row.AttemptCount++;
        row.LastFailureCode = NormalizeFailureCode(failureCode);
        row.LeaseId = null;
        row.LeaseExpiresUtc = null;
        row.UpdatedUtc = now;
        var terminal = row.AttemptCount >= _options.MaxDeliveryAttempts;
        if (terminal)
        {
            row.Status = AlertOutboxStatuses.DeadLetter;
        }
        else
        {
            row.Status = AlertOutboxStatuses.Pending;
            row.NextAttemptUtc = now + CalculateRetryDelay(
                row.AttemptCount,
                _options.RetryInitialDelayMilliseconds);
        }

        await db.SaveChangesAsync(ct);
        Interlocked.Exchange(ref _lastFailureUnixMilliseconds, now.ToUnixTimeMilliseconds());

        if (terminal)
        {
            Interlocked.Increment(ref _deadLetter);
            Interlocked.Increment(ref _terminalFailureTotal);
            ServerMetrics.AlertDeliveryFailedTotal.Add(1);
            _log.LogError(
                "Alerta {AlertEventId} enviada a dead-letter para {AlertTransport} después de {Attempts} intento(s); Code={FailureCode}",
                delivery.EventId,
                delivery.Transport,
                row.AttemptCount,
                row.LastFailureCode);
        }
        else
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Increment(ref _retryTotal);
            ServerMetrics.AlertDeliveryRetryTotal.Add(1);
            _log.LogWarning(
                "Reintento {NextAttempt}/{MaxAttempts} programado para alerta {AlertEventId} por {AlertTransport}; Code={FailureCode}",
                row.AttemptCount + 1,
                _options.MaxDeliveryAttempts,
                delivery.EventId,
                delivery.Transport,
                row.LastFailureCode);
            WakeWorkers();
        }
    }

    private async Task ReleaseLeaseAsync(AlertOutboxDelivery delivery, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await FindOwnedLeaseAsync(db, delivery, ct);
            if (row is null)
                return;

            row.Status = AlertOutboxStatuses.Pending;
            row.NextAttemptUtc = DateTimeOffset.UtcNow;
            row.LeaseId = null;
            row.LeaseExpiresUtc = null;
            row.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            Interlocked.Increment(ref _pending);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "No se liberó inmediatamente el lease de alerta {AlertEventId}; el lease expirará. ErrorType={ErrorType}",
                delivery.EventId,
                ex.GetType().Name);
        }
    }

    private static Task<AlertOutboxDelivery?> FindOwnedLeaseAsync(
        FileXferDbContext db,
        AlertOutboxDelivery delivery,
        CancellationToken ct) =>
        db.AlertOutboxDeliveries.FirstOrDefaultAsync(
            row => row.Id == delivery.Id &&
                   row.Status == AlertOutboxStatuses.Processing &&
                   row.LeaseId == delivery.LeaseId,
            ct);

    private async Task RefreshStateAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var pending = await db.AlertOutboxDeliveries.CountAsync(
                row => row.Status == AlertOutboxStatuses.Pending ||
                       (row.Status == AlertOutboxStatuses.Processing && row.LeaseExpiresUtc <= now),
                ct);
            var inFlight = await db.AlertOutboxDeliveries.CountAsync(
                row => row.Status == AlertOutboxStatuses.Processing && row.LeaseExpiresUtc > now,
                ct);
            var deadLetter = await db.AlertOutboxDeliveries.CountAsync(
                row => row.Status == AlertOutboxStatuses.DeadLetter,
                ct);
            var lastDelivered = await db.AlertOutboxDeliveries
                .Where(row => row.DeliveredUtc != null)
                .MaxAsync(row => (DateTimeOffset?)row.DeliveredUtc, ct);
            var lastFailure = await db.AlertOutboxDeliveries
                .Where(row => row.LastFailureCode != null)
                .MaxAsync(row => (DateTimeOffset?)row.UpdatedUtc, ct);

            Interlocked.Exchange(ref _pending, pending);
            Interlocked.Exchange(ref _inFlight, inFlight);
            Interlocked.Exchange(ref _deadLetter, deadLetter);
            if (lastDelivered is not null)
                Interlocked.Exchange(ref _lastDeliveredUnixMilliseconds, lastDelivered.Value.ToUnixTimeMilliseconds());
            if (lastFailure is not null)
                Interlocked.Exchange(ref _lastFailureUnixMilliseconds, lastFailure.Value.ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(
                ref _lastFailureUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            LogDatabaseWarning(ex, "No se pudo actualizar el estado del outbox SQL");
        }
    }

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = Interlocked.Read(ref _nextCleanupUnixMilliseconds);
        if (due != 0 && due > now.ToUnixTimeMilliseconds())
            return;

        await CleanupAsync(ct);
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Interlocked.Exchange(
            ref _nextCleanupUnixMilliseconds,
            now.AddHours(_options.CleanupIntervalHours).ToUnixTimeMilliseconds());

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var deliveredBefore = now.AddDays(-_options.DeliveredRetentionDays);
            var deadLetterBefore = now.AddDays(-_options.DeadLetterRetentionDays);
            int deliveredRemoved;
            int deadLetterRemoved;

            if (db.Database.IsRelational())
            {
                deliveredRemoved = await db.AlertOutboxDeliveries
                    .Where(row => row.Status == AlertOutboxStatuses.Delivered && row.UpdatedUtc < deliveredBefore)
                    .ExecuteDeleteAsync(ct);
                deadLetterRemoved = await db.AlertOutboxDeliveries
                    .Where(row => row.Status == AlertOutboxStatuses.DeadLetter && row.UpdatedUtc < deadLetterBefore)
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                var expired = await db.AlertOutboxDeliveries
                    .Where(row =>
                        (row.Status == AlertOutboxStatuses.Delivered && row.UpdatedUtc < deliveredBefore) ||
                        (row.Status == AlertOutboxStatuses.DeadLetter && row.UpdatedUtc < deadLetterBefore))
                    .ToListAsync(ct);
                deliveredRemoved = expired.Count(row => row.Status == AlertOutboxStatuses.Delivered);
                deadLetterRemoved = expired.Count - deliveredRemoved;
                db.AlertOutboxDeliveries.RemoveRange(expired);
                await db.SaveChangesAsync(ct);
            }

            if (deliveredRemoved + deadLetterRemoved > 0)
            {
                _log.LogInformation(
                    "Retención del outbox SQL eliminó {DeliveredRemoved} entregas y {DeadLetterRemoved} dead-letter expiradas",
                    deliveredRemoved,
                    deadLetterRemoved);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDatabaseWarning(ex, "Falló la retención del outbox SQL");
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        await _wakeSignal.WaitAsync(
            TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds),
            ct);
    }

    private void WakeWorkers()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            try
            {
                _wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Otro productor ya despertó a los workers.
            }
        }
    }

    private void RecordDeduplicated()
    {
        Interlocked.Increment(ref _deduplicatedTotal);
        ServerMetrics.AlertDeliveryDeduplicatedTotal.Add(1);
    }

    private void LogDatabaseWarning(Exception ex, string message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var previous = Interlocked.Read(ref _lastDatabaseWarningUnixMilliseconds);
        if (now - previous < TimeSpan.FromMinutes(1).TotalMilliseconds ||
            Interlocked.CompareExchange(ref _lastDatabaseWarningUnixMilliseconds, now, previous) != previous)
        {
            return;
        }

        _log.LogWarning("{Message}; ErrorType={ErrorType}", message, ex.GetType().Name);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static string ComputeDeduplicationKey(string title, string message) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{message}")));

    private static string NormalizeFailureCode(string? failureCode)
    {
        var value = string.IsNullOrWhiteSpace(failureCode) ? "unknown" : failureCode;
        return new string(value
            .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            .Take(128)
            .ToArray());
    }

    private static TimeSpan CalculateRetryDelay(int failedAttempt, int initialDelayMilliseconds)
    {
        var exponent = Math.Min(failedAttempt - 1, 5);
        var exponential = Math.Min(30_000, initialDelayMilliseconds * (1 << exponent));
        var jitter = Random.Shared.Next(0, Math.Max(1, exponential / 4));
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }

    private static DateTimeOffset? ReadTimestamp(ref long storage)
    {
        var value = Interlocked.Read(ref storage);
        return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private static int ToInt32Saturated(long value) =>
        value >= int.MaxValue ? int.MaxValue : value <= 0 ? 0 : (int)value;
}
