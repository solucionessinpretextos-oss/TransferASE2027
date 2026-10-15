using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FileXfer.Server.Options;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public sealed record AlertDeliverySnapshot(
    bool Enabled,
    bool IsHealthy,
    int Pending,
    int InFlight,
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

public sealed class AlertDeliveryQueue : BackgroundService, IAlertSink, IAlertDeliveryState
{
    private static readonly TimeSpan HealthFailureWindow = TimeSpan.FromMinutes(5);
    private readonly Channel<SecurityAlertEnvelope> _queue;
    private readonly LogAlertSink _logSink;
    private readonly IAlertTransport[] _transports;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertDeliveryQueue> _log;
    private readonly ConcurrentDictionary<string, long> _recent = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _pending;
    private long _inFlight;
    private long _enqueuedTotal;
    private long _deliveredTotal;
    private long _deduplicatedTotal;
    private long _droppedTotal;
    private long _retryTotal;
    private long _terminalFailureTotal;
    private long _lastDeliveredUnixMilliseconds;
    private long _lastFailureUnixMilliseconds;
    private long _lastDroppedUnixMilliseconds;
    private int _accepting = 1;

    public AlertDeliveryQueue(
        LogAlertSink logSink,
        IEnumerable<IAlertTransport> transports,
        IOptions<AlertOptions> options,
        ILogger<AlertDeliveryQueue> log)
    {
        _logSink = logSink;
        _options = options.Value;
        _log = log;
        _transports = transports.Where(transport => transport.IsConfigured).ToArray();
        _queue = Channel.CreateBounded<SecurityAlertEnvelope>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = _options.WorkerCount == 1,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        title = LogAlertSink.NormalizeTitle(title);
        message = LogAlertSink.Limit(message, 16_384);
        _ = _logSink.NotifySecurityEventAsync(title, message, CancellationToken.None);

        if (!_options.Enabled || Volatile.Read(ref _accepting) == 0)
            return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        if (IsDuplicate(title, message, now, out var deduplicationKey))
        {
            Interlocked.Increment(ref _deduplicatedTotal);
            ServerMetrics.AlertDeliveryDeduplicatedTotal.Add(1);
            return Task.CompletedTask;
        }

        var alert = new SecurityAlertEnvelope(Guid.NewGuid(), title, message, now);
        if (_queue.Writer.TryWrite(alert))
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Increment(ref _enqueuedTotal);
            ServerMetrics.AlertDeliveryEnqueuedTotal.Add(1);
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _droppedTotal);
        if (deduplicationKey is not null)
            _recent.TryRemove(deduplicationKey, out _);
        Interlocked.Exchange(ref _lastDroppedUnixMilliseconds, now.ToUnixTimeMilliseconds());
        ServerMetrics.AlertDeliveryDroppedTotal.Add(1);
        _log.LogCritical(
            "Cola de alertas llena; el evento {AlertEventId} quedó únicamente en el log local",
            alert.EventId);
        return Task.CompletedTask;
    }

    public AlertDeliverySnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var lastDelivered = ReadTimestamp(ref _lastDeliveredUnixMilliseconds);
        var lastFailure = ReadTimestamp(ref _lastFailureUnixMilliseconds);
        var lastDropped = ReadTimestamp(ref _lastDroppedUnixMilliseconds);
        var pending = checked((int)Interlocked.Read(ref _pending));
        var inFlight = checked((int)Interlocked.Read(ref _inFlight));
        var recentFailure = lastFailure is not null &&
                            now - lastFailure.Value <= HealthFailureWindow &&
                            (lastDelivered is null || lastDelivered < lastFailure);
        var recentDrop = lastDropped is not null && now - lastDropped.Value <= HealthFailureWindow;
        var saturated = pending + inFlight >= Math.Max(1, (int)(_options.QueueCapacity * 0.9));

        return new AlertDeliverySnapshot(
            _options.Enabled,
            !_options.Enabled || (!recentFailure && !recentDrop && !saturated),
            pending,
            inFlight,
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
        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            _drained.TrySetResult();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete();

        using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownDrainSeconds));
        try
        {
            await _drained.Task.WaitAsync(drainTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "El apagado agotó {DrainSeconds}s con {Pending} alerta(s) pendientes",
                _options.ShutdownDrainSeconds,
                Interlocked.Read(ref _pending));
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _inFlight);
                ServerMetrics.AlertQueueLatencyMs.Record(
                    Math.Max(0, (DateTimeOffset.UtcNow - alert.OccurredUtc).TotalMilliseconds));
                try
                {
                    await DeliverAsync(alert, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Apagado forzado después de agotar la ventana de drenado.
        }
    }

    private async Task DeliverAsync(SecurityAlertEnvelope alert, CancellationToken ct)
    {
        var results = await Task.WhenAll(
            _transports.Select(transport => DeliverToTransportAsync(alert, transport, ct)));
        if (results.All(result => result))
        {
            Interlocked.Increment(ref _deliveredTotal);
            Interlocked.Exchange(
                ref _lastDeliveredUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ServerMetrics.AlertDeliveryDeliveredTotal.Add(1);
        }
    }

    private async Task<bool> DeliverToTransportAsync(
        SecurityAlertEnvelope alert,
        IAlertTransport transport,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxDeliveryAttempts; attempt++)
        {
            AlertTransportResult result;
            try
            {
                result = await transport.SendAsync(alert, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                // No registrar mensajes completos: una excepción HTTP puede incluir
                // la URL del webhook y, con ella, un token en el query string.
                result = AlertTransportResult.Failed(ex.GetType().Name);
            }

            if (result.Success)
            {
                _log.LogInformation(
                    "Alerta {AlertEventId} entregada por {AlertTransport} en intento {Attempt}",
                    alert.EventId,
                    transport.Name,
                    attempt);
                return true;
            }

            if (attempt < _options.MaxDeliveryAttempts)
            {
                Interlocked.Increment(ref _retryTotal);
                ServerMetrics.AlertDeliveryRetryTotal.Add(1);
                var delay = CalculateRetryDelay(attempt, _options.RetryInitialDelayMilliseconds);
                _log.LogWarning(
                    "Reintento {NextAttempt}/{MaxAttempts} para alerta {AlertEventId} por {AlertTransport}; Code={FailureCode}",
                    attempt + 1,
                    _options.MaxDeliveryAttempts,
                    alert.EventId,
                    transport.Name,
                    result.FailureCode ?? "unknown");
                await Task.Delay(delay, ct);
            }
        }

        Interlocked.Increment(ref _terminalFailureTotal);
        Interlocked.Exchange(
            ref _lastFailureUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ServerMetrics.AlertDeliveryFailedTotal.Add(1);
        _log.LogError(
            "Alerta {AlertEventId} no pudo entregarse por {AlertTransport} después de {MaxAttempts} intento(s)",
            alert.EventId,
            transport.Name,
            _options.MaxDeliveryAttempts);
        return false;
    }

    private bool IsDuplicate(
        string title,
        string message,
        DateTimeOffset now,
        out string? key)
    {
        if (_options.DeduplicationWindowSeconds == 0)
        {
            key = null;
            return false;
        }

        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{message}"));
        key = Convert.ToHexString(keyBytes);
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var windowMilliseconds = checked(_options.DeduplicationWindowSeconds * 1000L);

        while (true)
        {
            if (!_recent.TryGetValue(key, out var previous))
            {
                if (_recent.TryAdd(key, nowMilliseconds))
                    break;
                continue;
            }

            if (nowMilliseconds - previous < windowMilliseconds)
                return true;

            if (_recent.TryUpdate(key, nowMilliseconds, previous))
                break;
        }

        if (_recent.Count > _options.QueueCapacity * 4)
        {
            var expiresBefore = nowMilliseconds - windowMilliseconds;
            foreach (var item in _recent)
            {
                if (item.Value < expiresBefore)
                    _recent.TryRemove(item.Key, out _);
            }
        }

        return false;
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
}
