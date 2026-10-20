// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/RateLockService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio antiabuso con ventanas de tasa en memoria.
// Interacción: Limita solicitudes de autenticación por IP y RFC.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Concurrent;
using FileXfer.Server.Options;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public sealed class RateLockService
{
    private const int MaxInMemoryCounters = 10_000;
    private readonly RateLimitOptions _opt;

    private readonly ConcurrentDictionary<string, WindowCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private long _rateCalls;

    public RateLockService(IOptions<RateLimitOptions> opt)
    {
        _opt = opt.Value;
    }

    private sealed class WindowCounter
    {
        public int Count;
        public DateTimeOffset WindowStart;
    }

    public bool TryRate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return false;

        var now = DateTimeOffset.UtcNow;
        var win = TimeSpan.FromSeconds(Math.Max(1, _opt.WindowSeconds));

        if ((Interlocked.Increment(ref _rateCalls) & 255) == 0)
            RemoveExpiredCounters(now, win);

        if (!_counters.TryGetValue(key, out var ctr))
        {
            if (_counters.Count >= MaxInMemoryCounters)
            {
                RemoveExpiredCounters(now, win);
                if (_counters.Count >= MaxInMemoryCounters)
                    return false;
            }

            ctr = _counters.GetOrAdd(key, _ => new WindowCounter { Count = 0, WindowStart = now });
        }

        lock (ctr) // grano fino por RFC
        {
            if (now - ctr.WindowStart >= win)
            {
                ctr.WindowStart = now;
                ctr.Count = 0;
            }

            if (ctr.Count >= Math.Max(1, _opt.PermitLimit))
                return false;

            ctr.Count++;
            return true;
        }
    }

    private void RemoveExpiredCounters(DateTimeOffset now, TimeSpan window)
    {
        foreach (var pair in _counters)
        {
            var expired = false;
            lock (pair.Value)
            {
                expired = now - pair.Value.WindowStart >= window + window;
            }

            if (expired)
                _counters.TryRemove(pair);
        }
    }
}
