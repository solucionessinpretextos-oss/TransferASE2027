using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TransferClienteASE.Modelos;

namespace TransferClienteASE.Servicios
{
    public sealed class EntradaDiarioTransferencia
    {
        public string JournalId { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string? PreparedPath { get; init; }
        public string? FileUuid { get; init; }
        public string? Sha256 { get; init; }
        public long OriginalLength { get; init; }
        public long PreparedLength { get; init; }
        public long ConfirmedBytes { get; init; }
        public bool WasRepaired { get; init; }
        public bool VerificationRequired { get; init; }
        public string Status { get; init; } = "Queued";
        public string? LastMessage { get; init; }
        public string? ReceiptId { get; init; }
        public string? TraceId { get; init; }
    }

    /// <summary>
    /// Diario transaccional local. Guarda solo metadatos de continuidad; nunca guarda JWT,
    /// contraseñas, certificados CER ni llaves KEY.
    /// </summary>
    public sealed class ServicioDiarioTransferencias
    {
        private readonly string _databasePath;
        private readonly string _recoveryRoot;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private bool _initialized;

        public ServicioDiarioTransferencias(string? applicationDataRoot = null)
        {
            var root = applicationDataRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASE", "TransferClienteASE");
            Directory.CreateDirectory(root);
            _databasePath = Path.Combine(root, "transfer-journal.db");
            _recoveryRoot = Path.Combine(root, "RecoveryFiles");
        }

        public async Task RegistrarEnColaAsync(
            ContextoRequerimiento context,
            string originalPath,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(originalPath))
                throw new ArgumentException("La ruta original es obligatoria.", nameof(originalPath));

            await EnsureInitializedAsync(ct);
            var fullPath = Path.GetFullPath(originalPath);
            var info = new FileInfo(fullPath);
            var originalLength = info.Exists ? info.Length : 0;
            var lastWriteUtc = info.Exists
                ? info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
                : null;
            var key = ContextKey.From(context);
            var journalId = CreateJournalId(fullPath, key);

            await using var connection = await OpenAsync(ct);
            string? obsoletePreparedPath = null;
            await using (var previous = connection.CreateCommand())
            {
                previous.CommandText = """
                    SELECT OriginalLength, OriginalLastWriteUtc, PreparedPath
                    FROM TransferJournal WHERE JournalId = $journalId;
                    """;
                previous.Parameters.AddWithValue("$journalId", journalId);
                await using var reader = await previous.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var previousLength = reader.GetInt64(0);
                    var previousWrite = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if ((previousLength != originalLength ||
                         !string.Equals(previousWrite, lastWriteUtc, StringComparison.Ordinal)) &&
                        !reader.IsDBNull(2))
                    {
                        obsoletePreparedPath = reader.GetString(2);
                    }
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TransferJournal (
                    JournalId, Rfc, DeviceId, OriginalPath, OriginalFileName,
                    OriginalLength, OriginalLastWriteUtc, Periodo, IdEnte, IdAuditoria,
                    IdActo, IdOficio, IdRequerimiento, IdResultado, Status,
                    CreatedUtc, UpdatedUtc)
                VALUES (
                    $journalId, $rfc, $deviceId, $originalPath, $originalFileName,
                    $originalLength, $originalLastWriteUtc, $periodo, $idEnte, $idAuditoria,
                    $idActo, $idOficio, $idRequerimiento, $idResultado, 'Queued',
                    $now, $now)
                ON CONFLICT(JournalId) DO UPDATE SET
                    OriginalFileName = excluded.OriginalFileName,
                    OriginalLength = excluded.OriginalLength,
                    OriginalLastWriteUtc = excluded.OriginalLastWriteUtc,
                    PreparedPath = CASE
                        WHEN TransferJournal.OriginalLength <> excluded.OriginalLength
                             OR IFNULL(TransferJournal.OriginalLastWriteUtc, '') <> IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN NULL ELSE TransferJournal.PreparedPath END,
                    PreparedLength = CASE
                        WHEN TransferJournal.OriginalLength <> excluded.OriginalLength
                             OR IFNULL(TransferJournal.OriginalLastWriteUtc, '') <> IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN 0 ELSE TransferJournal.PreparedLength END,
                    FileUuid = CASE
                        WHEN TransferJournal.OriginalLength <> excluded.OriginalLength
                             OR IFNULL(TransferJournal.OriginalLastWriteUtc, '') <> IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN NULL ELSE TransferJournal.FileUuid END,
                    Sha256 = CASE
                        WHEN TransferJournal.OriginalLength <> excluded.OriginalLength
                             OR IFNULL(TransferJournal.OriginalLastWriteUtc, '') <> IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN NULL ELSE TransferJournal.Sha256 END,
                    ConfirmedBytes = CASE
                        WHEN TransferJournal.OriginalLength <> excluded.OriginalLength
                             OR IFNULL(TransferJournal.OriginalLastWriteUtc, '') <> IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN 0 ELSE TransferJournal.ConfirmedBytes END,
                    Status = CASE
                        WHEN TransferJournal.Status = 'Completed'
                             AND TransferJournal.OriginalLength = excluded.OriginalLength
                             AND IFNULL(TransferJournal.OriginalLastWriteUtc, '') = IFNULL(excluded.OriginalLastWriteUtc, '')
                        THEN TransferJournal.Status
                        ELSE 'Queued'
                    END,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            BindIdentity(command, journalId, fullPath, key);
            command.Parameters.AddWithValue("$originalFileName", Path.GetFileName(fullPath));
            command.Parameters.AddWithValue("$originalLength", originalLength);
            command.Parameters.AddWithValue("$originalLastWriteUtc", (object?)lastWriteUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
            DeleteRecoveryFile(obsoletePreparedPath);
        }

        public async Task<IReadOnlyList<EntradaDiarioTransferencia>> ObtenerPendientesAsync(
            ContextoRequerimiento context,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct);
            var key = ContextKey.From(context);
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT JournalId, OriginalPath, PreparedPath, FileUuid, Sha256,
                       OriginalLength, PreparedLength, ConfirmedBytes, WasRepaired,
                       VerificationRequired, Status, LastMessage, ReceiptId, TraceId
                FROM TransferJournal
                WHERE Rfc = $rfc AND DeviceId = $deviceId
                  AND Periodo = $periodo AND IdEnte = $idEnte AND IdAuditoria = $idAuditoria
                  AND IdActo = $idActo AND IdOficio = $idOficio
                  AND IdRequerimiento = $idRequerimiento AND IdResultado = $idResultado
                  AND Status NOT IN ('Completed', 'Canceled')
                ORDER BY CreatedUtc, OriginalFileName;
                """;
            BindContext(command, key);
            var result = new List<EntradaDiarioTransferencia>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(ReadEntry(reader));
            return result;
        }

        public async Task<EntradaDiarioTransferencia?> ObtenerAsync(
            ContextoRequerimiento context,
            string originalPath,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct);
            var key = ContextKey.From(context);
            var fullPath = Path.GetFullPath(originalPath);
            var journalId = CreateJournalId(fullPath, key);
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT JournalId, OriginalPath, PreparedPath, FileUuid, Sha256,
                       OriginalLength, PreparedLength, ConfirmedBytes, WasRepaired,
                       VerificationRequired, Status, LastMessage, ReceiptId, TraceId
                FROM TransferJournal WHERE JournalId = $journalId;
                """;
            command.Parameters.AddWithValue("$journalId", journalId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
        }

        public async Task<string> ConservarCopiaPreparadaAsync(
            string fileUuid,
            string originalFileName,
            string sourcePath,
            CancellationToken ct)
        {
            if (!Guid.TryParseExact(fileUuid, "N", out _))
                throw new ArgumentException("FileUuid inválido.", nameof(fileUuid));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("No se encontró la copia preparada.", sourcePath);

            var directory = Path.Combine(_recoveryRoot, fileUuid);
            Directory.CreateDirectory(directory);
            var safeName = SanitizeFileName(originalFileName);
            var destination = Path.Combine(directory, safeName);
            var temporary = destination + ".tmp";

            await using (var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 1024 * 1024, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }

        public Task MarcarPreparadoAsync(
            ContextoRequerimiento context,
            string originalPath,
            string preparedPath,
            string fileUuid,
            string sha256,
            long preparedLength,
            bool wasRepaired,
            bool verificationRequired,
            int policyVersion,
            CancellationToken ct = default) =>
            UpdateAsync(
                context, originalPath,
                """
                PreparedPath = $preparedPath, FileUuid = $fileUuid, Sha256 = $sha256,
                PreparedLength = $preparedLength, WasRepaired = $wasRepaired,
                VerificationRequired = $verificationRequired, PolicyVersion = $policyVersion,
                Status = 'Ready', LastMessage = 'Archivo verificado y preparado para reanudación.'
                """,
                command =>
                {
                    command.Parameters.AddWithValue("$preparedPath", preparedPath);
                    command.Parameters.AddWithValue("$fileUuid", fileUuid);
                    command.Parameters.AddWithValue("$sha256", sha256);
                    command.Parameters.AddWithValue("$preparedLength", preparedLength);
                    command.Parameters.AddWithValue("$wasRepaired", wasRepaired ? 1 : 0);
                    command.Parameters.AddWithValue("$verificationRequired", verificationRequired ? 1 : 0);
                    command.Parameters.AddWithValue("$policyVersion", policyVersion);
                }, ct);

        public Task MarcarProgresoAsync(
            ContextoRequerimiento context,
            string originalPath,
            long confirmedBytes,
            string status,
            string message,
            CancellationToken ct = default) =>
            UpdateAsync(
                context, originalPath,
                "ConfirmedBytes = $confirmedBytes, Status = $status, LastMessage = $message",
                command =>
                {
                    command.Parameters.AddWithValue("$confirmedBytes", Math.Max(0, confirmedBytes));
                    command.Parameters.AddWithValue("$status", status);
                    command.Parameters.AddWithValue("$message", message);
                }, ct);

        public async Task CompletarAsync(
            ContextoRequerimiento context,
            string originalPath,
            long totalBytes,
            string? receiptId,
            string? traceId,
            CancellationToken ct = default)
        {
            var entry = await ObtenerAsync(context, originalPath, ct);
            await UpdateAsync(
                context, originalPath,
                """
                ConfirmedBytes = $confirmedBytes, Status = 'Completed',
                ReceiptId = $receiptId, TraceId = $traceId,
                LastMessage = 'Carga completada y confirmada por el servidor.', CompletedUtc = $now
                """,
                command =>
                {
                    command.Parameters.AddWithValue("$confirmedBytes", totalBytes);
                    command.Parameters.AddWithValue("$receiptId", (object?)receiptId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$traceId", (object?)traceId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                }, ct);

            DeleteRecoveryFile(entry?.PreparedPath);
        }

        public async Task CancelarAsync(
            ContextoRequerimiento context,
            string originalPath,
            CancellationToken ct = default)
        {
            var entry = await ObtenerAsync(context, originalPath, ct);
            await UpdateAsync(
                context, originalPath,
                "Status = 'Canceled', LastMessage = 'La carga fue retirada de la cola local.'",
                null, ct);
            DeleteRecoveryFile(entry?.PreparedPath);
        }

        private async Task UpdateAsync(
            ContextoRequerimiento context,
            string originalPath,
            string assignments,
            Action<SqliteCommand>? bind,
            CancellationToken ct)
        {
            await EnsureInitializedAsync(ct);
            var key = ContextKey.From(context);
            var journalId = CreateJournalId(Path.GetFullPath(originalPath), key);
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE TransferJournal SET {assignments}, UpdatedUtc = $updatedUtc WHERE JournalId = $journalId;";
            command.Parameters.AddWithValue("$journalId", journalId);
            command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            bind?.Invoke(command);
            await command.ExecuteNonQueryAsync(ct);
        }

        private async Task EnsureInitializedAsync(CancellationToken ct)
        {
            if (_initialized)
                return;

            await _initializationGate.WaitAsync(ct);
            try
            {
                if (_initialized)
                    return;
                await using var connection = await OpenAsync(ct, configure: false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    PRAGMA busy_timeout=5000;
                    CREATE TABLE IF NOT EXISTS TransferJournal (
                        JournalId TEXT NOT NULL PRIMARY KEY,
                        Rfc TEXT NOT NULL,
                        DeviceId TEXT NOT NULL,
                        OriginalPath TEXT NOT NULL,
                        OriginalFileName TEXT NOT NULL,
                        OriginalLength INTEGER NOT NULL DEFAULT 0,
                        OriginalLastWriteUtc TEXT NULL,
                        PreparedPath TEXT NULL,
                        PreparedLength INTEGER NOT NULL DEFAULT 0,
                        FileUuid TEXT NULL,
                        Sha256 TEXT NULL,
                        Periodo INTEGER NOT NULL,
                        IdEnte INTEGER NOT NULL,
                        IdAuditoria INTEGER NOT NULL,
                        IdActo INTEGER NOT NULL,
                        IdOficio INTEGER NOT NULL,
                        IdRequerimiento INTEGER NOT NULL,
                        IdResultado INTEGER NOT NULL,
                        ConfirmedBytes INTEGER NOT NULL DEFAULT 0,
                        WasRepaired INTEGER NOT NULL DEFAULT 0,
                        VerificationRequired INTEGER NOT NULL DEFAULT 1,
                        PolicyVersion INTEGER NOT NULL DEFAULT 0,
                        Status TEXT NOT NULL,
                        LastMessage TEXT NULL,
                        ReceiptId TEXT NULL,
                        TraceId TEXT NULL,
                        CreatedUtc TEXT NOT NULL,
                        UpdatedUtc TEXT NOT NULL,
                        CompletedUtc TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_TransferJournal_ContextStatus
                    ON TransferJournal (
                        Rfc, DeviceId, Periodo, IdEnte, IdAuditoria, IdActo,
                        IdOficio, IdRequerimiento, IdResultado, Status);
                    """;
                await command.ExecuteNonQueryAsync(ct);
                _initialized = true;
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task<SqliteConnection> OpenAsync(CancellationToken ct, bool configure = true)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString());
            await connection.OpenAsync(ct);
            if (configure)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
                await command.ExecuteNonQueryAsync(ct);
            }
            return connection;
        }

        private static EntradaDiarioTransferencia ReadEntry(SqliteDataReader reader) => new()
        {
            JournalId = reader.GetString(0),
            OriginalPath = reader.GetString(1),
            PreparedPath = reader.IsDBNull(2) ? null : reader.GetString(2),
            FileUuid = reader.IsDBNull(3) ? null : reader.GetString(3),
            Sha256 = reader.IsDBNull(4) ? null : reader.GetString(4),
            OriginalLength = reader.GetInt64(5),
            PreparedLength = reader.GetInt64(6),
            ConfirmedBytes = reader.GetInt64(7),
            WasRepaired = reader.GetInt64(8) != 0,
            VerificationRequired = reader.GetInt64(9) != 0,
            Status = reader.GetString(10),
            LastMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
            ReceiptId = reader.IsDBNull(12) ? null : reader.GetString(12),
            TraceId = reader.IsDBNull(13) ? null : reader.GetString(13)
        };

        private static void BindIdentity(
            SqliteCommand command,
            string journalId,
            string fullPath,
            ContextKey key)
        {
            command.Parameters.AddWithValue("$journalId", journalId);
            command.Parameters.AddWithValue("$originalPath", fullPath);
            BindContext(command, key);
        }

        private static void BindContext(SqliteCommand command, ContextKey key)
        {
            command.Parameters.AddWithValue("$rfc", key.Rfc);
            command.Parameters.AddWithValue("$deviceId", key.DeviceId);
            command.Parameters.AddWithValue("$periodo", key.Periodo);
            command.Parameters.AddWithValue("$idEnte", key.IdEnte);
            command.Parameters.AddWithValue("$idAuditoria", key.IdAuditoria);
            command.Parameters.AddWithValue("$idActo", key.IdActo);
            command.Parameters.AddWithValue("$idOficio", key.IdOficio);
            command.Parameters.AddWithValue("$idRequerimiento", key.IdRequerimiento);
            command.Parameters.AddWithValue("$idResultado", key.IdResultado);
        }

        private static string CreateJournalId(string fullPath, ContextKey key)
        {
            var canonical = string.Join("|",
                key.Rfc, key.DeviceId, key.Periodo, key.IdEnte, key.IdAuditoria,
                key.IdActo, key.IdOficio, key.IdRequerimiento, key.IdResultado,
                fullPath.ToUpperInvariant());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private static string SanitizeFileName(string fileName)
        {
            var name = Path.GetFileName(fileName);
            foreach (var invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "archivo.bin" : name;
        }

        private void DeleteRecoveryFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                var root = Path.GetFullPath(_recoveryRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(path);
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return;
                if (File.Exists(candidate))
                    File.Delete(candidate);
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
                    Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // La limpieza diferida no invalida un recibo ya confirmado.
            }
        }

        private readonly record struct ContextKey(
            string Rfc,
            string DeviceId,
            int Periodo,
            int IdEnte,
            int IdAuditoria,
            int IdActo,
            int IdOficio,
            int IdRequerimiento,
            int IdResultado)
        {
            public static ContextKey From(ContextoRequerimiento context)
            {
                var idResultado = context.ResultadoId > 0 ? context.ResultadoId : context.IdResultado;
                var idRequerimiento = idResultado > 0 ? 0 : context.Requerimiento?.Id ?? 0;
                return new ContextKey(
                    SesionAplicacion.Rfc.Trim().ToUpperInvariant(),
                    SesionAplicacion.DeviceId.Trim(),
                    context.Periodo,
                    context.Ente?.Id ?? 0,
                    context.Auditoria?.Id ?? 0,
                    context.Acto?.Id ?? 0,
                    context.Oficio?.Id ?? 0,
                    idRequerimiento,
                    idResultado);
            }
        }
    }
}
