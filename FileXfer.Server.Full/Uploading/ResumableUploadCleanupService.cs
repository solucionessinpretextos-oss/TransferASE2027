using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Uploading;

public sealed class ResumableUploadCleanupService : BackgroundService
{
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly FileAssembler _assembler;
    private readonly ILogger<ResumableUploadCleanupService> _logger;

    public ResumableUploadCleanupService(
        IDbContextFactory<FileXferDbContext> dbFactory,
        FileAssembler assembler,
        ILogger<ResumableUploadCleanupService> logger)
    {
        _dbFactory = dbFactory;
        _assembler = assembler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Falló la limpieza de cargas reanudables vencidas.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var expired = await db.Uploads
            .Where(upload =>
                (upload.Status == "Paused" || upload.Status == "Pending") &&
                upload.ResumeExpiresUtc != null &&
                upload.ResumeExpiresUtc < now)
            .OrderBy(upload => upload.ResumeExpiresUtc)
            .Take(100)
            .ToListAsync(ct);

        foreach (var upload in expired)
        {
            try
            {
                var leafId = upload.IdRequerimiento ?? upload.IdResultado;
                var storageKey = upload.IdEnte is > 0 && upload.IdAuditoria is > 0 &&
                                 upload.IdActo is > 0 && upload.IdOficio is > 0 && leafId is > 0
                    ? $"{upload.IdEnte.Value}/{upload.IdAuditoria.Value}/{upload.IdActo.Value}/{upload.IdOficio.Value}/{leafId.Value}"
                    : upload.Rfc;
                _assembler.DeleteIfExists(
                    storageKey,
                    upload.FileUuid,
                    Path.GetFileName(upload.StoredRelativePath));
                upload.Status = "Expired";
                upload.CompletedUtc = now;
                upload.LastActivityUtc = now;
                upload.LastError = "La ventana de reanudación expiró y la copia parcial fue eliminada.";
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "No se pudo limpiar la carga reanudable vencida. Uuid={Uuid}",
                    upload.FileUuid);
            }
        }

        if (expired.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
