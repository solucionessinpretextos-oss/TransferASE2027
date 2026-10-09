using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

public sealed class AuthStateCleanupService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RevokedTokenRetention = TimeSpan.FromDays(30);
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly ILogger<AuthStateCleanupService> _log;

    public AuthStateCleanupService(
        IDbContextFactory<FileXferDbContext> dbFactory,
        ILogger<AuthStateCleanupService> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falló la limpieza periódica del estado de autenticación.");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var revokedBefore = now - RevokedTokenRetention;

        if (db.Database.IsRelational())
        {
            var nonces = await db.Nonces
                .Where(nonce => nonce.ExpiresUtc < now)
                .ExecuteDeleteAsync(ct);
            var refreshTokens = await db.RefreshTokens
                .Where(token => token.ExpiresUtc < now ||
                                (token.IsRevoked && token.CreatedUtc < revokedBefore))
                .ExecuteDeleteAsync(ct);
            if (nonces > 0 || refreshTokens > 0)
            {
                _log.LogInformation(
                    "Limpieza de autenticación: Nonces={Nonces}; RefreshTokens={RefreshTokens}.",
                    nonces,
                    refreshTokens);
            }
            return;
        }

        var expiredNonces = await db.Nonces.Where(nonce => nonce.ExpiresUtc < now).ToListAsync(ct);
        var expiredTokens = await db.RefreshTokens
            .Where(token => token.ExpiresUtc < now ||
                            (token.IsRevoked && token.CreatedUtc < revokedBefore))
            .ToListAsync(ct);
        db.Nonces.RemoveRange(expiredNonces);
        db.RefreshTokens.RemoveRange(expiredTokens);
        if (expiredNonces.Count > 0 || expiredTokens.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
