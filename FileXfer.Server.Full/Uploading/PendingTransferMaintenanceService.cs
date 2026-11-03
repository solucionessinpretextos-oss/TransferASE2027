using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Uploading;

public sealed record PendingTransferDto(
    Guid UploadId, string FileName, string Rfc, string Status, long BytesReceived,
    long? ExpectedLength, DateTimeOffset LastActivityUtc, DateTimeOffset ResumeExpiresUtc,
    bool IsActive, bool IsExpired);

public sealed record PendingTransferPage(
    IReadOnlyList<PendingTransferDto> Items, int TotalCount, int Page, int PageSize);

public sealed record PendingTransferCleanupResult(
    int Removed, int Skipped, int Failed, long BytesReclaimed, string Message);

public sealed class PendingTransferMaintenanceService
{
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly FileAssembler _assembler;
    private readonly UploadSessionStore _sessions;
    private readonly StorageOptions _storage;
    private readonly ILogger<PendingTransferMaintenanceService> _logger;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private int _scanOffset;

    public PendingTransferMaintenanceService(
        IDbContextFactory<FileXferDbContext> dbFactory,
        FileAssembler assembler,
        UploadSessionStore sessions,
        StorageOptions storage,
        ILogger<PendingTransferMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _assembler = assembler;
        _sessions = sessions;
        _storage = storage;
        _logger = logger;
    }

    public DateTimeOffset GetExpiration(UploadEntry upload) =>
        upload.ResumeExpiresUtc ??
        (upload.LastActivityUtc == default ? upload.CreatedUtc : upload.LastActivityUtc)
            .AddHours(_storage.ResumeRetentionHours);

    public async Task<PendingTransferPage> GetPendingAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || page > 1_000_000 || pageSize is not (10 or 25 or 50 or 100))
            throw new ArgumentException("Página no válida. Use 10, 25, 50 o 100 archivos por página.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Uploads.AsNoTracking()
            .Where(upload => (upload.Status == "Pending" || upload.Status == "Paused") &&
                upload.CompletedUtc == null);
        var count = await query.CountAsync(ct);
        var uploads = await query.OrderBy(upload => upload.CreatedUtc).ThenBy(upload => upload.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return new PendingTransferPage(uploads.Select(upload => new PendingTransferDto(
            upload.Id, upload.OriginalFileName ?? upload.FileUuid, upload.Rfc, upload.Status,
            upload.BytesReceived, upload.ExpectedLength,
            upload.LastActivityUtc == default ? upload.CreatedUtc : upload.LastActivityUtc,
            GetExpiration(upload), _sessions.IsActive(upload.FileUuid), GetExpiration(upload) <= now))
            .ToArray(), count, page, pageSize);
    }

    public async Task<PendingTransferCleanupResult> RemoveAsync(
        IReadOnlyCollection<Guid> uploadIds, string reason, string actor, string? ip, CancellationToken ct)
    {
        if (uploadIds.Count is < 1 or > 100 || uploadIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("Seleccione entre 1 y 100 transferencias válidas.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            throw new ArgumentException("Indique el motivo de eliminación (entre 5 y 500 caracteres).");
        return await RemoveCoreAsync(uploadIds.Distinct().ToArray(), false, reason.Trim(), actor, ip, ct);
    }

    public async Task<PendingTransferCleanupResult> CleanupExpiredAsync(
        string actor, string? ip, CancellationToken ct, string? reason = null)
    {
        if (reason is not null && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500))
            throw new ArgumentException("Indique el motivo de limpieza (entre 5 y 500 caracteres).");
        // Retain a bounded scan cursor across passes so protected or temporarily
        // failing rows cannot starve newer expired partials forever. Removed rows
        // leave this query; only skipped/failed rows advance the offset. OFFSET/FETCH
        // is supported on SQL Server 2014 and avoids collection parameters/OPENJSON.
        await _cleanupGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ids = await ExpiredQuery(db, DateTimeOffset.UtcNow, _storage.ResumeRetentionHours)
                .Skip(_scanOffset).Select(upload => upload.Id).Take(1000).ToArrayAsync(ct);
            var result = await RemoveCoreAsync(ids, true,
                reason?.Trim() ?? "La ventana de reanudación expiró.", actor, ip, ct);
            _scanOffset = ids.Length < 1000 || _scanOffset > int.MaxValue - 1000
                ? 0
                : _scanOffset + result.Skipped + result.Failed;
            return result;
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    public static IQueryable<UploadEntry> ExpiredQuery(
        FileXferDbContext db, DateTimeOffset now, int retentionHours)
    {
        var cutoff = now.AddHours(-retentionHours);
        return db.Uploads.AsNoTracking()
            .Where(upload => (upload.Status == "Paused" || upload.Status == "Pending") &&
                ((upload.ResumeExpiresUtc != null && upload.ResumeExpiresUtc <= now) ||
                 (upload.ResumeExpiresUtc == null &&
                   ((upload.LastActivityUtc != default && upload.LastActivityUtc <= cutoff) ||
                    (upload.LastActivityUtc == default && upload.CreatedUtc <= cutoff)))))
            .OrderBy(upload => upload.CreatedUtc).ThenBy(upload => upload.Id);
    }

    private async Task<PendingTransferCleanupResult> RemoveCoreAsync(
        IReadOnlyCollection<Guid> ids, bool expiredOnly, string reason,
        string actor, string? ip, CancellationToken ct)
    {
        var removed = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var candidate = await db.Uploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
                if (candidate is null || !_sessions.TryAcquireMaintenance(candidate.FileUuid, out var lease))
                {
                    skipped++;
                    continue;
                }
                using (lease)
                {
                    // Reload after obtaining exclusivity; an upload can have completed
                    // between selecting the page and requesting its removal.
                    var upload = await db.Uploads.SingleOrDefaultAsync(u => u.Id == id, ct);
                    var now = DateTimeOffset.UtcNow;
                    if (upload is null || upload.Status is not ("Pending" or "Paused") ||
                        upload.CompletedUtc != null || upload.ReceiptId != null ||
                        string.IsNullOrWhiteSpace(upload.StoredRelativePath) ||
                        (expiredOnly && GetExpiration(upload) > now))
                    {
                        skipped++;
                        continue;
                    }

                    var normalizedStoredPath = upload.StoredRelativePath.Replace("\\", "/");
                    var caseInsensitiveStoredPath = normalizedStoredPath.ToUpperInvariant();
                    if (await db.UploadReceipts.AnyAsync(receipt => receipt.UploadId == upload.Id, ct) ||
                        await db.Uploads.AnyAsync(other => other.Id != upload.Id &&
                            other.StoredRelativePath.Replace("\\", "/").ToUpper() == caseInsensitiveStoredPath, ct))
                    {
                        // A legacy row can be inconsistent or share a physical file
                        // with another UUID. Never delete evidence referenced elsewhere.
                        skipped++;
                        continue;
                    }

                    var leafId = upload.IdRequerimiento ?? upload.IdResultado;
                    var storageKey = upload.IdEnte is > 0 && upload.IdAuditoria is > 0 &&
                        upload.IdActo is > 0 && upload.IdOficio is > 0 && leafId is > 0
                        ? $"{upload.IdEnte.Value}/{upload.IdAuditoria.Value}/{upload.IdActo.Value}/{upload.IdOficio.Value}/{leafId.Value}"
                        : upload.Rfc;
                    var fileName = Path.GetFileName(upload.StoredRelativePath);
                    var resolvedPath = _assembler.ResolveAbsolutePath(storageKey, upload.FileUuid, fileName);
                    var resolvedRelativePath = _assembler.GetStoredRelativePath(resolvedPath).Replace("\\", "/");
                    if (!string.Equals(resolvedRelativePath, normalizedStoredPath, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    var physicalBytes = _assembler.GetPersistedLength(storageKey, upload.FileUuid, fileName);
                    _assembler.DeleteIfExists(storageKey, upload.FileUuid, fileName);
                    upload.Status = expiredOnly ? "Expired" : "Cancelled";
                    upload.CompletedUtc = now;
                    upload.LastActivityUtc = now;
                    upload.ResumeExpiresUtc = null;
                    upload.LastError = $"{(expiredOnly ? "Limpieza por vencimiento" : "Eliminación manual")}: {reason} Actor: {actor}. Copia parcial eliminada; registro de auditoría conservado.";
                    db.AuditLogs.Add(new AuditLog
                    {
                        Id = Guid.NewGuid(), TimestampUtc = now, ActorRfc = actor == "system" ? null : actor,
                        Action = expiredOnly ? "PendingTransferExpired" : "PendingTransferCancelled",
                        Ip = ip, Details = $"UploadId={upload.Id:D}; FileUuid={upload.FileUuid}; Actor={actor}; Bytes={physicalBytes}; Reason={reason}"
                    });
                    await db.SaveChangesAsync(ct);
                    removed++;
                    bytes = checked(bytes + physicalBytes);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failed++;
                _logger.LogWarning(exception, "No se pudo eliminar el pendiente. UploadId={UploadId}", id);
            }
        }
        return Result(removed, skipped, failed, bytes);
    }

    private static PendingTransferCleanupResult Result(int removed, int skipped, int failed, long bytes) =>
        new(removed, skipped, failed, bytes,
            $"{removed} pendiente(s) eliminado(s); {skipped} omitido(s) por estar activos, finalizados o aún vigentes; {failed} con error. El historial y los documentos completados se conservan.");
}
