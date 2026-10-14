using System.Security.Cryptography;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

public sealed record PreparedFileDownload(
    FileStream Stream,
    Guid UploadId,
    string FileName,
    string Sha256,
    long Length);

public sealed class FileDownloadPreparationException : Exception
{
    public FileDownloadPreparationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface ISecureFileDownloadService
{
    Task<PreparedFileDownload> PrepareAsync(
        ArchivoLocalDownloadDescriptor descriptor,
        CancellationToken cancellationToken);
}

public sealed class SecureFileDownloadService : ISecureFileDownloadService
{
    private const int BufferSize = 128 * 1024;
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly string _storageRoot;

    public SecureFileDownloadService(
        IDbContextFactory<FileXferDbContext> dbFactory,
        StorageOptions storageOptions)
    {
        _dbFactory = dbFactory;
        _storageRoot = Path.GetFullPath(storageOptions.BasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public async Task<PreparedFileDownload> PrepareAsync(
        ArchivoLocalDownloadDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var upload = await db.Uploads.AsNoTracking()
            .Where(candidate =>
                candidate.Rfc == descriptor.Rfc &&
                candidate.StoredRelativePath == descriptor.StoredRelativePath &&
                candidate.Status == "Completed" &&
                candidate.Sha256Server != null)
            .OrderByDescending(candidate => candidate.CompletedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (upload is null || string.IsNullOrWhiteSpace(upload.Sha256Server))
        {
            throw new FileDownloadPreparationException(
                "integrity_metadata_unavailable",
                "El archivo no tiene evidencia interna suficiente para una descarga segura.");
        }

        var fullPath = ResolveWithinStorage(descriptor.StoredRelativePath);
        RejectReparsePoints(fullPath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            throw new FileDownloadPreparationException(
                "file_missing",
                "El archivo registrado ya no está disponible en el almacenamiento.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileDownloadPreparationException(
                "file_missing",
                "El archivo registrado ya no está disponible en el almacenamiento.");
        }

        try
        {
            var expectedLength = upload.ExpectedLength ?? upload.BytesReceived;
            if (expectedLength < 0 || stream.Length != expectedLength)
            {
                throw new FileDownloadPreparationException(
                    "length_mismatch",
                    "El archivo cambió de tamaño después de su recepción y fue bloqueado.");
            }

            using var sha256 = SHA256.Create();
            var actualHash = Convert.ToHexString(
                    await sha256.ComputeHashAsync(stream, cancellationToken))
                .ToLowerInvariant();

            if (!FixedTimeHexEquals(upload.Sha256Server, actualHash))
            {
                throw new FileDownloadPreparationException(
                    "hash_mismatch",
                    "La integridad del archivo no coincide con su recepción original y fue bloqueado.");
            }

            stream.Position = 0;
            var safeName = Path.GetFileName(descriptor.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = upload.Id.ToString("N") + ".bin";

            return new PreparedFileDownload(
                stream,
                upload.Id,
                safeName,
                actualHash,
                stream.Length);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private string ResolveWithinStorage(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new FileDownloadPreparationException(
                "invalid_storage_path",
                "La referencia de almacenamiento del archivo no es válida.");
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var fullPath = Path.GetFullPath(Path.Combine(new[] { _storageRoot }.Concat(segments).ToArray()));
        var rootPrefix = _storageRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileDownloadPreparationException(
                "invalid_storage_path",
                "La referencia de almacenamiento queda fuera del volumen autorizado.");
        }

        return fullPath;
    }

    private void RejectReparsePoints(string fullPath)
    {
        if (Directory.Exists(_storageRoot) &&
            (File.GetAttributes(_storageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileDownloadPreparationException(
                "unsafe_storage_link",
                "La raíz de almacenamiento es un enlace no autorizado.");
        }

        var relative = Path.GetRelativePath(_storageRoot, fullPath);
        var current = _storageRoot;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new FileDownloadPreparationException(
                        "unsafe_storage_link",
                        "El archivo atraviesa un enlace de almacenamiento no autorizado.");
                }
            }
        }
    }

    private static bool FixedTimeHexEquals(string expected, string actual)
    {
        try
        {
            var expectedBytes = Convert.FromHexString(expected);
            var actualBytes = Convert.FromHexString(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
