// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Uploading/FileAssembler.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Subsistema de carga de archivos: administra streaming gRPC, escritura física, hash, estado y registro final.
// Interacción: Recibe chunks desde TransferClienteASE, arma archivos en storage, valida integridad y persiste metadata en BuDi/FileXfer.
// Regla de mantenimiento: No marcar una carga como completa si falla la persistencia de negocio; diferenciar archivo recibido de archivo catalogado.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileXfer.Server.Uploading
{
    public sealed class FileAssembler
    {
        private readonly StorageOptions _storage;
        private readonly IDbContextFactory<FileXferDbContext> _dbf;
        private readonly ILogger<FileAssembler> _log;
        private readonly UploadSessionStore _sessions;

        private const int MaxSafeFullPathChars = 240;

        private static readonly HashSet<string> WindowsReservedBaseNames = new(
            new[]
            {
                "CON","PRN","AUX","NUL",
                "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
                "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

        private const int StreamBufferSize = 1024 * 1024; // 1MB
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
        private const long FlushEveryBytes = 16L * 1024L * 1024L; // 16MB

        public FileAssembler(
            StorageOptions storage,
            IDbContextFactory<FileXferDbContext> dbf,
            ILogger<FileAssembler> log,
            UploadSessionStore sessions)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _dbf = dbf ?? throw new ArgumentNullException(nameof(dbf));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        private static string SanitizePathSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var s = value.Trim();

            if (string.Equals(s, ".", StringComparison.Ordinal) ||
                string.Equals(s, "..", StringComparison.Ordinal))
            {
                return "_";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }

            s = s.Replace(':', '_')
                 .Replace(Path.DirectorySeparatorChar, '_')
                 .Replace(Path.AltDirectorySeparatorChar, '_');

            s = s.TrimEnd(' ', '.');

            return s;
        }

        private static string BuildSafeFileNameForWindows(
            string dir,
            string safeName,
            string fileUuid,
            ILogger? log)
        {
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = $"{fileUuid}.bin";

            var ext = Path.GetExtension(safeName) ?? string.Empty; // incluye el punto
            var baseName = Path.GetFileNameWithoutExtension(safeName);

            if (string.IsNullOrWhiteSpace(baseName))
                baseName = fileUuid;

            if (WindowsReservedBaseNames.Contains(baseName))
                baseName = "_" + baseName;

            baseName = baseName.TrimEnd(' ', '.');

            var candidate = baseName + ext;

            const int maxFileNameChars = 255;
            if (candidate.Length > maxFileNameChars)
            {
                var suffix = "_" + (fileUuid.Length >= 8 ? fileUuid[..8] : fileUuid);
                var allowedBase = maxFileNameChars - ext.Length - suffix.Length;
                if (allowedBase < 1)
                {
                    suffix = string.Empty;
                    allowedBase = maxFileNameChars - ext.Length;
                }

                if (allowedBase < 1)
                {
                    candidate = (fileUuid.Length > maxFileNameChars ? fileUuid[..maxFileNameChars] : fileUuid);
                }
                else
                {
                    var trimmed = baseName.Length > allowedBase ? baseName[..allowedBase] : baseName;
                    trimmed = trimmed.TrimEnd(' ', '.');
                    candidate = trimmed + suffix + ext;
                }
            }

            var full = Path.Combine(dir, candidate);
            if (full.Length > MaxSafeFullPathChars)
            {
                var allowedNameChars = MaxSafeFullPathChars - (dir.Length + 1);
                if (allowedNameChars < 1)
                {
                    log?.LogWarning(
                        "Ruta excede el umbral ({Len}>{Max}) incluso sin margen. DirLen={DirLen}, FileUuid={FileUuid}",
                        full.Length, MaxSafeFullPathChars, dir.Length, fileUuid);
                    return candidate;
                }

                var suffix = "_" + (fileUuid.Length >= 8 ? fileUuid[..8] : fileUuid);
                if (allowedNameChars < (ext.Length + suffix.Length + 1))
                {
                    suffix = "_" + (fileUuid.Length >= 4 ? fileUuid[..4] : fileUuid);
                }
                if (allowedNameChars < (ext.Length + suffix.Length + 1))
                {
                    suffix = string.Empty;
                }

                var allowedBase = allowedNameChars - ext.Length - suffix.Length;
                if (allowedBase < 1)
                {
                    var fallback = (fileUuid.Length > allowedNameChars ? fileUuid[..allowedNameChars] : fileUuid);
                    log?.LogWarning(
                        "Nombre truncado extremo por longitud de ruta. Original={Original} -> Fallback={Fallback}",
                        safeName, fallback);
                    return fallback;
                }

                var trimmed = baseName.Length > allowedBase ? baseName[..allowedBase] : baseName;
                trimmed = trimmed.TrimEnd(' ', '.');

                var shortened = trimmed + suffix + ext;
                var full2 = Path.Combine(dir, shortened);

                if (!string.Equals(candidate, shortened, StringComparison.Ordinal))
                {
                    log?.LogWarning(
                        "Nombre de archivo truncado para evitar problemas de ruta. Original={Original} -> Stored={Stored} (FullLen={Len})",
                        safeName, shortened, full2.Length);
                }

                return shortened;
            }

            return candidate;
        }

        public string ResolveAbsolutePath(string rfcOrLogicalPath, string fileUuid, string? fileName)
        {
            var raw = rfcOrLogicalPath ?? string.Empty;
            var parts = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            var segments = new List<string>();
            foreach (var part in parts)
            {
                var safe = SanitizePathSegment(part);
                if (!string.IsNullOrWhiteSpace(safe))
                    segments.Add(safe);
            }

            if (segments.Count == 0)
                segments.Add("UNKNOWN");

            var name = string.IsNullOrWhiteSpace(fileName) ? $"{fileUuid}.bin" : fileName;
            var safeName = SanitizePathSegment(name);

            var storageRoot = Path.GetFullPath(_storage.BasePath);
            Directory.CreateDirectory(storageRoot);
            RejectReparsePoint(new DirectoryInfo(storageRoot), "la raíz de almacenamiento");
            var dirParts = new List<string> { storageRoot };
            dirParts.AddRange(segments);

            var dir = Path.GetFullPath(Path.Combine(dirParts.ToArray()));
            EnsureWithinStorage(dir, storageRoot);
            Directory.CreateDirectory(dir);
            EnsureDirectoryChainHasNoReparsePoints(dir, storageRoot);

            var safeNameFinal = BuildSafeFileNameForWindows(dir, safeName, fileUuid, _log);
            var absolutePath = Path.GetFullPath(Path.Combine(dir, safeNameFinal));
            EnsureWithinStorage(absolutePath, storageRoot);
            if (File.Exists(absolutePath))
                RejectReparsePoint(new FileInfo(absolutePath), "el archivo de carga");
            return absolutePath;
        }

        public string GetStoredRelativePath(string absolutePath)
        {
            var storageRoot = Path.GetFullPath(_storage.BasePath);
            var fullPath = Path.GetFullPath(absolutePath);
            EnsureWithinStorage(fullPath, storageRoot);
            var rel = Path.GetRelativePath(storageRoot, fullPath);
            return rel.Replace('\\', '/');
        }

        private static void EnsureWithinStorage(string candidate, string storageRoot)
        {
            var normalizedRoot = Path.GetFullPath(storageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCandidate = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !normalizedCandidate.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("La ruta de almacenamiento calculada salió de la raíz configurada.");
            }
        }

        private static void EnsureDirectoryChainHasNoReparsePoints(
            string directory,
            string storageRoot)
        {
            var normalizedRoot = Path.GetFullPath(storageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            EnsureWithinStorage(normalizedDirectory, normalizedRoot);
            RejectReparsePoint(new DirectoryInfo(normalizedRoot), "la raíz de almacenamiento");

            var relative = Path.GetRelativePath(normalizedRoot, normalizedDirectory);
            if (relative == ".")
                return;

            var current = normalizedRoot;
            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                RejectReparsePoint(new DirectoryInfo(current), "la ruta de almacenamiento");
            }
        }

        private static void RejectReparsePoint(FileSystemInfo item, string description)
        {
            item.Refresh();
            if (!item.Exists)
                throw new IOException($"No existe {description} después de prepararla.");
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Se rechazó {description} porque atraviesa un vínculo o punto de reanálisis.");
            }
        }

        public void ResetForRetry(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName)
        {
            _sessions.RemoveWriteState(fileUuid);
            var path = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        public long GetPersistedLength(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName)
        {
            _sessions.RemoveWriteState(fileUuid);
            var path = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        /// <summary>
        /// Fuerza a almacenamiento estable todos los bytes del escritor activo. El valor
        /// devuelto es el único offset que el servidor puede publicar como confirmado.
        /// </summary>
        public async Task<long> FlushToDiskAsync(string fileUuid, CancellationToken ct)
        {
            if (!_sessions.TryGetWriteState(fileUuid, out var state))
                throw new InvalidOperationException("No existe un escritor activo para confirmar la carga.");

            await state.WriteGate.WaitAsync(ct);
            try
            {
                await state.Stream.FlushAsync(ct);
                state.Stream.Flush(flushToDisk: true);
                state.MarkFlushed();
                return state.Stream.Length;
            }
            finally
            {
                state.WriteGate.Release();
            }
        }

        /// <summary>
        /// Reconcilia el archivo físico con el offset confirmado en SQL. Una cola que pudo
        /// quedar en caché pero no alcanzó su checkpoint se elimina antes de reanudar.
        /// </summary>
        public long ReconcileToCommittedLength(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName,
            long committedLength)
        {
            if (committedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(committedLength));

            _sessions.RemoveWriteState(fileUuid);
            var path = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);
            if (!File.Exists(path))
                return 0;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: StreamBufferSize,
                options: FileOptions.SequentialScan);
            var safeLength = Math.Min(stream.Length, committedLength);
            if (stream.Length != safeLength)
            {
                stream.SetLength(safeLength);
                stream.Flush(flushToDisk: true);
            }
            return safeLength;
        }

        public void DeleteIfExists(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName)
        {
            _sessions.RemoveWriteState(fileUuid);
            var path = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        public async Task AppendAsync(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName,
            long offset,
            byte[] data,
            CancellationToken ct)
        {
            if (rfcOrLogicalPath == null) throw new ArgumentNullException(nameof(rfcOrLogicalPath));
            if (fileUuid == null) throw new ArgumentNullException(nameof(fileUuid));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (data.Length > _storage.MaxChunkBytes)
                throw new InvalidDataException("El bloque excede el tamaño máximo configurado.");
            if (_storage.WouldExceedUploadSizeLimit(offset, data.LongLength))
                throw new InvalidDataException("El archivo excede el tamaño máximo configurado.");

            var path = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var state = _sessions.GetOrCreateWriteState(fileUuid, () =>
            {
                var fs = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: StreamBufferSize,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                IncrementalHash? hash = null;
                try { hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); } catch {  }

                return new UploadWriteState(fileUuid, path, fs, hash);
            });

            await state.WriteGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                if (state.Stream.Length < offset)
                    throw new InvalidDataException("No se permiten offsets dispersos en una carga.");

                if (state.Stream.Position != offset)
                    state.Stream.Position = offset;

                if (state.Hash != null && state.HashValid)
                {
                    if (offset != state.NextExpectedOffset)
                    {
                        state.InvalidateHash();
                    }
                }

                if (data.Length > 0)
                {
                    await state.Stream.WriteAsync(data, 0, data.Length, ct);

                    if (state.Hash != null && state.HashValid)
                    {
                        state.Hash.AppendData(data, 0, data.Length);
                        state.AdvanceExpectedOffset(offset + data.LongLength);
                    }

                    state.Touch();
                    state.AddFlushBytes(data.Length);
                }

                var needFlush =
                    state.BytesSinceFlush >= FlushEveryBytes ||
                    (DateTimeOffset.UtcNow - state.LastFlushUtc) >= FlushInterval;

                if (needFlush)
                {
                    await state.Stream.FlushAsync(ct);
                    state.MarkFlushed();
                }
            }
            finally
            {
                state.WriteGate.Release();
            }
        }

        public async Task<(bool accepted, string reason, long bytes, bool retried)> WriteChunkAsync(
            string rfc,
            string fileUuid,
            string? fileName,
            long offset,
            byte[] data,
            bool eof,
            string? sha256Hex,
            CancellationToken ct)
        {
            await AppendAsync(rfc, fileUuid, fileName, offset, data, ct);
            return (true, "ok", data?.LongLength ?? 0L, false);
        }

        public async Task<string> ComputeSha256Async(
            string rfcOrLogicalPath,
            string fileUuid,
            string? fileName,
            CancellationToken ct)
        {
            if (_sessions.TryGetWriteState(fileUuid, out var state))
            {
                await state.WriteGate.WaitAsync(ct);
                try
                {
                    if (!string.IsNullOrWhiteSpace(state.FinalSha256Hex))
                        return state.FinalSha256Hex!;

                    await state.Stream.FlushAsync(ct);
                    state.Stream.Flush(flushToDisk: true);

                    // La aceptación final se calcula sobre los bytes releídos del archivo
                    // persistido, no sobre el flujo de entrada. Así un fallo de escritura o
                    // vaciado no puede producir un hash "correcto" de datos que nunca llegaron
                    // al almacenamiento.
                    using var persisted = new FileStream(
                        state.StagingFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: StreamBufferSize,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var persistedSha = SHA256.Create();
                    var persistedHash = await persistedSha.ComputeHashAsync(persisted, ct);
                    var persistedHex = Convert.ToHexString(persistedHash).ToLowerInvariant();
                    state.SetFinalHash(persistedHex);
                    return persistedHex;
                }
                finally
                {
                    state.WriteGate.Release();
                }
            }

            var abs = ResolveAbsolutePath(rfcOrLogicalPath, fileUuid, fileName);

            using var fs = new FileStream(
                abs,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: StreamBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var sha = SHA256.Create();
            var hash2 = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash2).ToLowerInvariant();
        }

        public async Task AuditAsync(
            string rfc,
            string action,
            string ip,
            string details,
            CancellationToken ct)
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                ActorRfc = rfc,
                Action = action,
                Ip = ip,
                Details = details
            });

            await db.SaveChangesAsync(ct);
        }
        public void ReleaseLock(string rfcOrLogicalPath, string fileUuid)
        {
            if (string.IsNullOrWhiteSpace(fileUuid))
                return;

            try
            {
                _sessions.RemoveWriteState(fileUuid);
            }
            catch
            {
            }
        }

    }
}
