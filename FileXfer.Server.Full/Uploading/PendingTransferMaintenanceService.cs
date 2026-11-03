using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Uploading;

public sealed record PendingTransferDto(
    Guid UploadId, string FileName, string Rfc, string Status, long BytesReceived,
    long? ExpectedLength, DateTimeOffset LastActivityUtc, DateTimeOffset ResumeExpiresUtc,
    bool IsActive, bool IsExpired, string? ProtectionReason, bool CanReconcile, string? ProtectionCode);
public sealed record PendingTransferPage(IReadOnlyList<PendingTransferDto> Items, int TotalCount, int Page, int PageSize);
public sealed record PendingTransferCleanupItem(
    Guid UploadId, string FileName, string Outcome, string Code, string Message, long BytesReclaimed);
public sealed record PendingTransferCleanupResult(
    int Removed, int Skipped, int Failed, long BytesReclaimed, string Message,
    int RetiredPreservingFile, IReadOnlyList<PendingTransferCleanupItem> Items);
public sealed record PendingTransferDiagnostics(
    int TotalExpired, int Examined, int SharedPaths, int ContextMismatches, int Active,
    int ProtectedEvidence, int EligibleForCleanup, bool IsSampled, DateTimeOffset CheckedUtc);

public sealed class PendingTransferMaintenanceService
{
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly FileAssembler _assembler;
    private readonly UploadSessionStore _sessions;
    private readonly StorageOptions _storage;
    private readonly ILogger<PendingTransferMaintenanceService> _logger;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly SemaphoreSlim _diagnosticsGate = new(1, 1);
    private PendingTransferDiagnostics? _diagnostics;
    private int _scanOffset;

    public PendingTransferMaintenanceService(
        IDbContextFactory<FileXferDbContext> dbFactory, FileAssembler assembler,
        UploadSessionStore sessions, StorageOptions storage, ILogger<PendingTransferMaintenanceService> logger)
    {
        _dbFactory = dbFactory; _assembler = assembler; _sessions = sessions; _storage = storage; _logger = logger;
    }

    public DateTimeOffset GetExpiration(UploadEntry upload) => upload.ResumeExpiresUtc ??
        (upload.LastActivityUtc == default ? upload.CreatedUtc : upload.LastActivityUtc).AddHours(_storage.ResumeRetentionHours);

    public async Task<PendingTransferPage> GetPendingAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || page > 1_000_000 || pageSize is not (10 or 25 or 50 or 100))
            throw new ArgumentException("Página no válida. Use 10, 25, 50 o 100 archivos por página.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Uploads.AsNoTracking().Where(upload =>
            (upload.Status == "Pending" || upload.Status == "Paused") && upload.CompletedUtc == null);
        var count = await query.CountAsync(ct);
        var uploads = await query.OrderBy(upload => upload.CreatedUtc).ThenBy(upload => upload.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var assessments = await AssessAsync(db, uploads, ct);
        var now = DateTimeOffset.UtcNow;
        return new PendingTransferPage(uploads.Select(upload =>
        {
            var assessment = assessments[upload.Id];
            return new PendingTransferDto(upload.Id, upload.OriginalFileName ?? upload.FileUuid, upload.Rfc, upload.Status,
                upload.BytesReceived, upload.ExpectedLength,
                upload.LastActivityUtc == default ? upload.CreatedUtc : upload.LastActivityUtc,
                GetExpiration(upload), assessment.IsActive, GetExpiration(upload) <= now,
                assessment.Reason, assessment.CanReconcile, assessment.Code);
        }).ToArray(), count, page, pageSize);
    }

    public async Task<PendingTransferDiagnostics> GetDiagnosticsAsync(CancellationToken ct)
    {
        var cached = _diagnostics;
        if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedUtc < TimeSpan.FromSeconds(30)) return cached;
        await _diagnosticsGate.WaitAsync(ct);
        try
        {
            cached = _diagnostics;
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedUtc < TimeSpan.FromSeconds(30)) return cached;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var query = ExpiredQuery(db, now, _storage.ResumeRetentionHours).Where(upload => upload.CompletedUtc == null);
            var count = await query.CountAsync(ct);
            var uploads = await query.Take(100).ToListAsync(ct);
            var assessed = (await AssessAsync(db, uploads, ct)).Values.ToArray();
            return _diagnostics = new(count, assessed.Length,
                assessed.Count(item => item.Code == "SharedPhysicalFile"),
                assessed.Count(item => item.Code is "ContextPathMismatch" or "InvalidStoredPath"),
                assessed.Count(item => item.IsActive), assessed.Count(item => item.Code == "ProtectedEvidence"),
                assessed.Count(item => item.Code is null), count > assessed.Length, now);
        }
        finally { _diagnosticsGate.Release(); }
    }

    public Task<PendingTransferCleanupResult> RemoveAsync(
        IReadOnlyCollection<Guid> uploadIds, string reason, string actor, string? ip, CancellationToken ct)
    {
        ValidateManualRequest(uploadIds, reason);
        return RemoveCoreAsync(uploadIds.Distinct().ToArray(), false, false, reason.Trim(), actor, ip, ct);
    }

    public Task<PendingTransferCleanupResult> ReconcileAsync(
        IReadOnlyCollection<Guid> uploadIds, string reason, string actor, string? ip, CancellationToken ct)
    {
        ValidateManualRequest(uploadIds, reason);
        return RemoveCoreAsync(uploadIds.Distinct().ToArray(), false, true, reason.Trim(), actor, ip, ct);
    }

    public async Task<PendingTransferCleanupResult> CleanupExpiredAsync(
        string actor, string? ip, CancellationToken ct, string? reason = null)
    {
        if (reason is not null && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500))
            throw new ArgumentException("Indique el motivo de limpieza (entre 5 y 500 caracteres).");
        await _cleanupGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ids = await ExpiredQuery(db, DateTimeOffset.UtcNow, _storage.ResumeRetentionHours)
                .Skip(_scanOffset).Select(upload => upload.Id).Take(1000).ToArrayAsync(ct);
            var result = await RemoveCoreAsync(ids, true, false,
                reason?.Trim() ?? "La ventana de reanudación expiró.", actor, ip, ct);
            _scanOffset = ids.Length < 1000 || _scanOffset > int.MaxValue - 1000
                ? 0 : _scanOffset + result.Skipped + result.Failed;
            return result;
        }
        finally { _cleanupGate.Release(); }
    }

    public static IQueryable<UploadEntry> ExpiredQuery(FileXferDbContext db, DateTimeOffset now, int retentionHours)
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

    private static void ValidateManualRequest(IReadOnlyCollection<Guid> ids, string reason)
    {
        if (ids.Count is < 1 or > 100 || ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Seleccione entre 1 y 100 transferencias válidas.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            throw new ArgumentException("Indique el motivo (entre 5 y 500 caracteres).");
    }

    private async Task<PendingTransferCleanupResult> RemoveCoreAsync(
        IReadOnlyCollection<Guid> ids, bool expiredOnly, bool reconcileOnly, string reason,
        string actor, string? ip, CancellationToken ct)
    {
        var items = new List<PendingTransferCleanupItem>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var name = "Archivo no localizado";
            var leases = new List<IDisposable>();
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var candidate = await db.Uploads.AsNoTracking().SingleOrDefaultAsync(upload => upload.Id == id, ct);
                if (candidate is null) { AddSkipped("NotFound", "La transferencia ya no existe."); continue; }
                name = candidate.OriginalFileName ?? candidate.FileUuid;
                if (!_sessions.TryAcquireMaintenance(candidate.FileUuid, out var lease))
                {
                    AddSkipped("ActiveTransfer", "La transferencia está activa o en otra operación. Espere a que termine o se pause.");
                    continue;
                }
                leases.Add(lease!);
                var upload = await db.Uploads.SingleOrDefaultAsync(row => row.Id == id, ct);
                if (upload is null || upload.Status is not ("Pending" or "Paused") || upload.CompletedUtc != null)
                { AddSkipped("AlreadyClosed", "La transferencia ya está cerrada. Se conserva su historial."); continue; }
                if (upload.ReceiptId != null || await db.UploadReceipts.AnyAsync(receipt => receipt.UploadId == id, ct))
                {
                    AddSkipped("ProtectedEvidence", "Existe un recibo asociado a esta transferencia. Requiere conciliación documental; no puede retirarse como parcial.");
                    continue;
                }
                var now = DateTimeOffset.UtcNow;
                if (expiredOnly && GetExpiration(upload) > now)
                { AddSkipped("StillResumable", "El plazo de reanudación sigue vigente."); continue; }

                var normalizedPath = Normalize(upload.StoredRelativePath);
                var references = string.IsNullOrWhiteSpace(normalizedPath) ? new List<PathReference>()
                    : await FindReferences(db, normalizedPath, id).ToListAsync(ct);
                var aliasBusy = false;
                // Non-blocking leases in stable UUID order exclude active aliases and
                // keep resume/cleanup serialized while the record is retired.
                foreach (var reference in references.OrderBy(other => other.FileUuid, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(reference.FileUuid, upload.FileUuid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_sessions.TryAcquireMaintenance(reference.FileUuid, out var referenceLease))
                    { aliasBusy = true; break; }
                    leases.Add(referenceLease!);
                }
                if (aliasBusy)
                {
                    AddSkipped("ActiveSharedReference", "Otra transferencia que utiliza el mismo archivo está activa o en mantenimiento. Se conserva todo hasta que termine.");
                    continue;
                }

                var matchesContext = MatchesContext(upload, out var pathReason);
                if (!reconcileOnly && !matchesContext)
                {
                    AddSkipped(string.IsNullOrWhiteSpace(normalizedPath) ? "InvalidStoredPath" : "ContextPathMismatch",
                        pathReason + " Use «Conciliar conservando archivo» para retirar únicamente este registro pendiente.");
                    continue;
                }
                var preserveFile = reconcileOnly || references.Count > 0;
                long physicalBytes = 0;
                if (!preserveFile)
                {
                    var storageKey = StorageKey(upload);
                    var fileName = Path.GetFileName(upload.StoredRelativePath);
                    var resolved = _assembler.ResolveAbsolutePath(storageKey, upload.FileUuid, fileName);
                    if (!string.Equals(_assembler.GetStoredRelativePath(resolved).Replace('\\', '/'),
                        upload.StoredRelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        AddSkipped("ContextPathMismatch", "El nombre o ruta registrados cambian al aplicar las reglas del almacenamiento. El archivo se conserva; use «Conciliar conservando archivo» para retirar únicamente este registro.");
                        continue;
                    }
                    physicalBytes = _assembler.GetPersistedLength(storageKey, upload.FileUuid, fileName);
                    _assembler.DeleteIfExists(storageKey, upload.FileUuid, fileName);
                }
                var expired = expiredOnly || (reconcileOnly && GetExpiration(upload) <= now);
                upload.Status = expired ? "Expired" : "Cancelled";
                upload.CompletedUtc = now;
                upload.LastActivityUtc = now;
                upload.ResumeExpiresUtc = null;
                var prefix = reconcileOnly ? "Conciliación manual" : expiredOnly ? "Limpieza por vencimiento" : "Eliminación manual";
                var physicalAction = preserveFile ? "Archivo conservado íntegro; solo se retiró el registro pendiente" : "Copia parcial eliminada";
                upload.LastError = $"{prefix}: {reason} Actor: {actor}. {physicalAction}; auditoría conservada.";
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(), TimestampUtc = now, ActorRfc = actor == "system" ? null : actor,
                    Action = reconcileOnly ? "PendingTransferReconciled" : expired ? "PendingTransferExpired" : "PendingTransferCancelled",
                    Ip = ip, Details = $"UploadId={id:D}; FileUuid={upload.FileUuid}; Actor={actor}; Bytes={physicalBytes}; PhysicalAction={(preserveFile ? "Preserved" : "Deleted")}; SharedReferences={references.Count}; Reason={reason}"
                });
                await db.SaveChangesAsync(ct);
                items.Add(new(id, name, preserveFile ? "RetiredPreservingFile" : "Removed",
                    preserveFile ? reconcileOnly ? "MetadataReconciled" : "SharedPhysicalFilePreserved" : "PartialDeleted",
                    preserveFile ? $"Registro pendiente retirado como {upload.Status}. El archivo físico se conserva íntegro{(references.Count > 0 ? $" porque tiene {references.Count} referencia(s) adicional(es)" : " para revisión documental")}."
                        : $"Copia parcial eliminada; transferencia cerrada como {upload.Status}.", physicalBytes));
                _diagnostics = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "No se pudo procesar el pendiente. UploadId={UploadId}", id);
                var code = exception is UnauthorizedAccessException ? "StoragePermissionDenied"
                    : exception is IOException ? "StorageIoFailure"
                    : exception is DbUpdateException ? "DatabaseUpdateFailed" : "MaintenanceFailed";
                var message = code switch
                {
                    "StoragePermissionDenied" => "La cuenta del servicio no tiene permiso para acceder o borrar la copia parcial. Revise el diagnóstico de almacenamiento.",
                    "StorageIoFailure" => "El almacenamiento devolvió un error de acceso, bloqueo o disponibilidad. Revise el diagnóstico y vuelva a intentar.",
                    "DatabaseUpdateFailed" => "No se pudo guardar el cierre y su auditoría en SQL. Revise la base de datos antes de reintentar.",
                    _ => "No se completó el mantenimiento. Use el identificador de esta transferencia para consultar el error en los registros del servidor."
                };
                items.Add(new(id, name, "Failed", code, message, 0));
            }
            finally { foreach (var held in leases.AsEnumerable().Reverse()) held.Dispose(); }
            void AddSkipped(string code, string message) => items.Add(new(id, name, "Skipped", code, message, 0));
        }
        return Result(items);
    }

    private async Task<Dictionary<Guid, Assessment>> AssessAsync(
        FileXferDbContext db, IReadOnlyCollection<UploadEntry> uploads, CancellationToken ct)
    {
        if (uploads.Count == 0) return new();
        var ids = uploads.Select(upload => upload.Id).ToArray();
        var paths = uploads.Select(upload => Normalize(upload.StoredRelativePath)).Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        var receipts = (await db.UploadReceipts.AsNoTracking().Where(receipt => EF.Constant(ids).Contains(receipt.UploadId))
            .Select(receipt => receipt.UploadId).ToListAsync(ct)).ToHashSet();
        var references = paths.Length == 0 ? new List<PathReference>() : await db.Uploads.AsNoTracking()
            .Where(other => EF.Constant(paths).Contains(other.StoredRelativePath.Replace("\\", "/").ToUpper()))
            .Select(other => new PathReference(other.Id, other.FileUuid, other.StoredRelativePath)).ToListAsync(ct);
        var result = new Dictionary<Guid, Assessment>();
        foreach (var upload in uploads)
        {
            var samePath = references.Where(other => other.Id != upload.Id && Normalize(other.StoredRelativePath) == Normalize(upload.StoredRelativePath)).ToArray();
            var active = _sessions.IsActive(upload.FileUuid) || samePath.Any(other => _sessions.IsActive(other.FileUuid));
            if (upload.CompletedUtc != null || upload.ReceiptId != null || receipts.Contains(upload.Id))
                result[upload.Id] = new("ProtectedEvidence", "Tiene cierre o recibo documental; requiere revisión de evidencia.", false, active);
            else if (active)
                result[upload.Id] = new("ActiveTransfer", "La transferencia o una referencia al mismo archivo está activa. Espere a que termine o se pause.", false, true);
            else if (!MatchesContext(upload, out var pathReason))
                result[upload.Id] = new(string.IsNullOrWhiteSpace(upload.StoredRelativePath) ? "InvalidStoredPath" : "ContextPathMismatch",
                    pathReason + " Puede conciliarse conservando íntegro el archivo físico.", true, false);
            else if (samePath.Length > 0)
                result[upload.Id] = new("SharedPhysicalFile", $"Comparte archivo físico con {samePath.Length} referencia(s). La limpieza retirará solo este registro pendiente y conservará el archivo.", true, false);
            else result[upload.Id] = new(null, null, false, false);
        }
        return result;
    }

    private static IQueryable<PathReference> FindReferences(FileXferDbContext db, string normalizedPath, Guid excludedId) =>
        db.Uploads.AsNoTracking().Where(other => other.Id != excludedId && other.StoredRelativePath.Replace("\\", "/").ToUpper() == normalizedPath)
            .Select(other => new PathReference(other.Id, other.FileUuid, other.StoredRelativePath));

    private static bool MatchesContext(UploadEntry upload, out string reason)
    {
        // Listing, diagnostics and metadata-only reconciliation must never create
        // directories or open an uncertain stored path.
        var path = (upload.StoredRelativePath ?? "").Replace('\\', '/');
        var parts = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || parts.Length < 2 ||
            parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            IsWindowsReservedName(parts[^1]))
        {
            reason = "La ruta registrada es vacía, absoluta o inválida. No se abrirá ni eliminará ningún archivo.";
            return false;
        }
        if (!string.Equals(string.Join('/', parts[..^1]), StorageKey(upload).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            reason = "La ruta registrada no coincide con el contexto documental actual. La limpieza automática conserva el archivo para evitar borrar una ubicación equivocada.";
            return false;
        }
        reason = "";
        return true;
    }

    private static string StorageKey(UploadEntry upload)
    {
        var leafId = upload.IdRequerimiento ?? upload.IdResultado;
        return upload.IdEnte is > 0 && upload.IdAuditoria is > 0 && upload.IdActo is > 0 && upload.IdOficio is > 0 && leafId is > 0
            ? $"{upload.IdEnte.Value}/{upload.IdAuditoria.Value}/{upload.IdActo.Value}/{upload.IdOficio.Value}/{leafId.Value}" : upload.Rfc;
    }
    private static string Normalize(string? path) => (path ?? "").Replace("\\", "/").ToUpperInvariant();
    private static bool IsWindowsReservedName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }
    private sealed record PathReference(Guid Id, string FileUuid, string StoredRelativePath);
    private sealed record Assessment(string? Code, string? Reason, bool CanReconcile, bool IsActive);

    private static PendingTransferCleanupResult Result(IReadOnlyList<PendingTransferCleanupItem> items)
    {
        var removed = items.Count(item => item.Outcome == "Removed");
        var preserved = items.Count(item => item.Outcome == "RetiredPreservingFile");
        var skipped = items.Count(item => item.Outcome == "Skipped");
        var failed = items.Count(item => item.Outcome == "Failed");
        return new(removed, skipped, failed, items.Sum(item => item.BytesReclaimed),
            $"{removed} copia(s) parcial(es) eliminada(s); {preserved} registro(s) pendiente(s) retirado(s) conservando su archivo; {skipped} omitido(s); {failed} con error. Consulte la causa y la acción de cada archivo.",
            preserved, items);
    }
}
