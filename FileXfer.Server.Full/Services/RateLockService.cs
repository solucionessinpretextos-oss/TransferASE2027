// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/RateLockService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio anti-fuerza-bruta/lockout.
// Interacción: Controla intentos fallidos de autenticación para evitar abuso.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Concurrent;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public sealed class RateLockService
{
    private const int MaxInMemoryCounters = 10_000;
    private readonly IDbContextFactory<FileXferDbContext> _dbf;
    private readonly RateLimitOptions _opt;
    private readonly ILogger<RateLockService> _log;

    private readonly ConcurrentDictionary<string, WindowCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private long _rateCalls;

    public RateLockService(
        IDbContextFactory<FileXferDbContext> dbf,
        IOptions<RateLimitOptions> opt,
        ILogger<RateLockService> log)
    {
        _dbf = dbf;
        _opt = opt.Value;
        _log = log;
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

    public async Task<bool> IsLockedAsync(string rfc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lo = await db.Lockouts.AsNoTracking().SingleOrDefaultAsync(x => x.Rfc == rfc);
        if (lo == null) return false;

        var now = DateTimeOffset.UtcNow;
        if (lo.LockedUntilUtc.HasValue && lo.LockedUntilUtc.Value > now)
            return true;

        return false;
    }

    public async Task RegisterFailureAsync(string rfc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lo = await db.Lockouts.SingleOrDefaultAsync(x => x.Rfc == rfc);
        var now = DateTimeOffset.UtcNow;

        if (lo == null)
        {
            lo = new Lockout
            {
                Id = Guid.NewGuid(),
                Rfc = rfc,
                FailCount = 1,
                LockedUntilUtc = null,
                UpdatedUtc = now
            };
            db.Lockouts.Add(lo);
        }
        else
        {
            var failureWindow = TimeSpan.FromMinutes(Math.Max(1, _opt.LockoutMinutes));
            if ((lo.LockedUntilUtc.HasValue && lo.LockedUntilUtc.Value <= now) ||
                now - lo.UpdatedUtc > failureWindow)
            {
                lo.FailCount = 1;
                lo.LockedUntilUtc = null;
            }
            else
            {
                lo.FailCount += 1;
            }
            lo.UpdatedUtc = now;
        }

        if (lo.FailCount >= Math.Max(1, _opt.LockoutThreshold))
        {
            lo.LockedUntilUtc = now.AddMinutes(Math.Max(1, _opt.LockoutMinutes));
            _log.LogWarning("RFC {Rfc} bloqueado hasta {Until} por {Fails} fallos.", rfc, lo.LockedUntilUtc, lo.FailCount);
        }

        await db.SaveChangesAsync();
    }

    public async Task RegisterSuccessAsync(string rfc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lo = await db.Lockouts.SingleOrDefaultAsync(x => x.Rfc == rfc);
        if (lo == null) return;

        lo.FailCount = 0;
        lo.LockedUntilUtc = null;
        lo.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
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
