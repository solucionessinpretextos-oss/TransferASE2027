using System.Text.RegularExpressions;
using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

public sealed record FilePolicySnapshot(
    Guid Id,
    string Extension,
    bool IsAllowed,
    bool RequiresVerification,
    long? MaxFileBytes,
    int Version,
    DateTimeOffset UpdatedUtc,
    string UpdatedBy,
    string ChangeReason);

public sealed record FilePolicyChange(
    bool IsAllowed,
    bool RequiresVerification,
    long? MaxFileBytes,
    int ExpectedVersion,
    string Reason);

public sealed class FilePolicyConflictException : InvalidOperationException
{
    public FilePolicyConflictException(string message) : base(message) { }
}

public interface IFilePolicyService
{
    Task EnsureSeededAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FilePolicySnapshot>> GetAllAsync(CancellationToken ct = default);
    Task<FilePolicySnapshot?> FindAsync(string extension, CancellationToken ct = default);
    Task<FilePolicySnapshot> RequireAllowedAsync(string fileName, long totalBytes, CancellationToken ct = default);
    Task<FilePolicySnapshot> UpsertAsync(string extension, FilePolicyChange change, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<FileTypePolicyRevision>> GetHistoryAsync(string extension, CancellationToken ct = default);
    Task<FilePolicySnapshot> RollbackAsync(Guid revisionId, int expectedVersion, string reason, string actor, CancellationToken ct = default);
}

public sealed class FilePolicyService : IFilePolicyService
{
    private static readonly Regex ExtensionPattern = new(
        "^\\.[a-z0-9][a-z0-9+_-]{0,22}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VerifiedByDefault = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".xltx", ".odt", ".ods", ".odp", ".ots",
        ".epub", ".xps", ".oxps", ".vsdx", ".xml", ".xsd", ".json", ".svg", ".svgz",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".txt", ".csv",
        ".tsv", ".tab", ".psv", ".md", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".conf", ".properties", ".sql", ".ics", ".vcf", ".asc", ".csr", ".cer"
    };

    private static readonly string[] KnownButBlockedByDefault =
    {
        ".doc", ".xls", ".xlt", ".ppt", ".rtf", ".wps", ".xlsm", ".xltm", ".docm",
        ".pptm", ".numbers", ".ai", ".eps", ".cdr", ".dxf", ".dwg", ".vsd", ".sqlite",
        ".db", ".sqlite3", ".mdb", ".bak", ".accdb", ".gpg", ".eml", ".msg", ".mbox",
        ".pst", ".ost", ".sua"
    };

    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;

    public FilePolicyService(IDbContextFactory<FileXferDbContext> dbFactory) =>
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.FileTypePolicies.AnyAsync(ct))
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var extension in VerifiedByDefault.Concat(KnownButBlockedByDefault).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var allowed = VerifiedByDefault.Contains(extension);
            var policy = new FileTypePolicy
            {
                Id = Guid.NewGuid(),
                Extension = extension,
                IsAllowed = allowed,
                RequiresVerification = true,
                MaxFileBytes = null,
                Version = 1,
                UpdatedUtc = now,
                UpdatedBy = "system-seed",
                ChangeReason = allowed
                    ? "Política inicial: formato verificable habilitado."
                    : "Política inicial: formato sin verificación segura bloqueado."
            };
            db.FileTypePolicies.Add(policy);
            db.FileTypePolicyRevisions.Add(ToRevision(policy));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FilePolicySnapshot>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var policies = await db.FileTypePolicies
            .AsNoTracking()
            .OrderBy(policy => policy.Extension)
            .ToListAsync(ct);
        return policies.Select(ToSnapshot).ToArray();
    }

    public async Task<FilePolicySnapshot?> FindAsync(string extension, CancellationToken ct = default)
    {
        var normalized = NormalizeExtension(extension);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var policy = await db.FileTypePolicies
            .AsNoTracking()
            .Where(policy => policy.Extension == normalized)
            .SingleOrDefaultAsync(ct);
        return policy is null ? null : ToSnapshot(policy);
    }

    public async Task<FilePolicySnapshot> RequireAllowedAsync(
        string fileName,
        long totalBytes,
        CancellationToken ct = default)
    {
        string extension;
        try
        {
            extension = NormalizeExtension(Path.GetExtension(fileName));
        }
        catch (ArgumentException)
        {
            throw new FilePolicyConflictException("El archivo no tiene una extensión válida configurada.");
        }
        var policy = await FindAsync(extension, ct);
        if (policy is null)
            throw new FilePolicyConflictException($"La extensión {extension} no tiene una política configurada y no se puede recibir.");
        if (!policy.IsAllowed)
            throw new FilePolicyConflictException($"La extensión {extension} está deshabilitada por la política de transferencia.");
        if (policy.MaxFileBytes.HasValue && totalBytes > policy.MaxFileBytes.Value)
        {
            throw new FilePolicyConflictException(
                $"El archivo excede el límite de {FormatBytes(policy.MaxFileBytes.Value)} configurado para {extension}.");
        }
        return policy;
    }

    public async Task<FilePolicySnapshot> UpsertAsync(
        string extension,
        FilePolicyChange change,
        string actor,
        CancellationToken ct = default)
    {
        var normalized = NormalizeExtension(extension);
        ValidateChange(change, actor);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var policy = await db.FileTypePolicies.SingleOrDefaultAsync(item => item.Extension == normalized, ct);
        if (policy is null)
        {
            if (change.ExpectedVersion != 0)
                throw new FilePolicyConflictException("La política no existe; ExpectedVersion debe ser 0 para crearla.");
            policy = new FileTypePolicy
            {
                Id = Guid.NewGuid(),
                Extension = normalized,
                Version = 1
            };
            db.FileTypePolicies.Add(policy);
        }
        else
        {
            if (change.ExpectedVersion != policy.Version)
                throw new FilePolicyConflictException("La política cambió desde que se abrió. Recargue la página antes de guardar.");
            policy.Version++;
        }

        Apply(policy, change, actor);
        db.FileTypePolicyRevisions.Add(ToRevision(policy));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new FilePolicyConflictException("La política fue modificada simultáneamente. Recargue y vuelva a intentarlo.");
        }

        return ToSnapshot(policy);
    }

    public async Task<IReadOnlyList<FileTypePolicyRevision>> GetHistoryAsync(
        string extension,
        CancellationToken ct = default)
    {
        var normalized = NormalizeExtension(extension);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.FileTypePolicyRevisions
            .AsNoTracking()
            .Where(revision => revision.Extension == normalized)
            .OrderByDescending(revision => revision.Version)
            .Take(100)
            .ToListAsync(ct);
    }

    public async Task<FilePolicySnapshot> RollbackAsync(
        Guid revisionId,
        int expectedVersion,
        string reason,
        string actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 512)
            throw new ArgumentException("El motivo del rollback debe contener entre 5 y 512 caracteres.", nameof(reason));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var revision = await db.FileTypePolicyRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == revisionId, ct)
            ?? throw new KeyNotFoundException("No se encontró la revisión solicitada.");
        var policy = await db.FileTypePolicies.SingleAsync(item => item.Id == revision.PolicyId, ct);
        if (policy.Version != expectedVersion)
            throw new FilePolicyConflictException("La política cambió antes del rollback. Recargue el historial.");

        policy.IsAllowed = revision.IsAllowed;
        policy.RequiresVerification = revision.RequiresVerification;
        policy.MaxFileBytes = revision.MaxFileBytes;
        policy.Version++;
        policy.UpdatedUtc = DateTimeOffset.UtcNow;
        policy.UpdatedBy = actor;
        policy.ChangeReason = $"Rollback a versión {revision.Version}: {reason.Trim()}";
        db.FileTypePolicyRevisions.Add(ToRevision(policy));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new FilePolicyConflictException("La política fue modificada simultáneamente. Recargue y vuelva a intentarlo.");
        }
        return ToSnapshot(policy);
    }

    public static string NormalizeExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (!normalized.StartsWith(".", StringComparison.Ordinal))
            normalized = "." + normalized;
        if (!ExtensionPattern.IsMatch(normalized))
            throw new ArgumentException("La extensión debe iniciar con punto y contener únicamente letras, números, +, _ o -.");
        return normalized;
    }

    private static void ValidateChange(FilePolicyChange change, string actor)
    {
        if (change.ExpectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(change.ExpectedVersion));
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
            throw new ArgumentException("El actor administrativo no es válido.", nameof(actor));
        if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Trim().Length is < 5 or > 512)
            throw new ArgumentException("El motivo del cambio debe contener entre 5 y 512 caracteres.", nameof(change.Reason));
        if (change.MaxFileBytes is <= 0)
            throw new ArgumentException("El límite por archivo debe ser positivo.", nameof(change.MaxFileBytes));
    }

    private static void Apply(FileTypePolicy policy, FilePolicyChange change, string actor)
    {
        policy.IsAllowed = change.IsAllowed;
        policy.RequiresVerification = change.RequiresVerification;
        policy.MaxFileBytes = change.MaxFileBytes;
        policy.UpdatedUtc = DateTimeOffset.UtcNow;
        policy.UpdatedBy = actor;
        policy.ChangeReason = change.Reason.Trim();
    }

    private static FileTypePolicyRevision ToRevision(FileTypePolicy policy) => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = policy.Id,
        Extension = policy.Extension,
        IsAllowed = policy.IsAllowed,
        RequiresVerification = policy.RequiresVerification,
        MaxFileBytes = policy.MaxFileBytes,
        Version = policy.Version,
        ChangedUtc = policy.UpdatedUtc,
        ChangedBy = policy.UpdatedBy,
        ChangeReason = policy.ChangeReason
    };

    private static FilePolicySnapshot ToSnapshot(FileTypePolicy policy) => new(
        policy.Id,
        policy.Extension,
        policy.IsAllowed,
        policy.RequiresVerification,
        policy.MaxFileBytes,
        policy.Version,
        policy.UpdatedUtc,
        policy.UpdatedBy,
        policy.ChangeReason);

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
        ? $"{bytes / 1024d / 1024d:0.##} MB"
        : $"{bytes / 1024d:0.##} KB";
}
