// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Uploading/UploadSessionStore.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Subsistema de carga de archivos: administra streaming gRPC, escritura física, hash, estado y registro final.
// Interacción: Recibe chunks desde TransferClienteASE, arma archivos en storage, valida integridad y persiste metadata en BuDi/FileXfer.
// Regla de mantenimiento: No marcar una carga como completa si falla la persistencia de negocio; diferenciar archivo recibido de archivo catalogado.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using FileXfer.Server.Options;

namespace FileXfer.Server.Uploading
{
    public sealed class UploadSessionStore
    {
        private readonly StorageOptions _storage;
        private readonly object _sessionGate = new();
        private readonly ConcurrentDictionary<string, UploadSessionInfo> _sessions =
            new(StringComparer.OrdinalIgnoreCase);

        public UploadSessionStore(StorageOptions storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public IReadOnlyDictionary<string, UploadSessionInfo> Sessions => _sessions;

        public UploadStartResult TryBegin(string fileUuid, string rfc)
        {
            lock (_sessionGate)
            {
                if (_sessions.ContainsKey(fileUuid))
                    return UploadStartResult.DuplicateUuid;
                if (_sessions.Count >= _storage.MaxConcurrentUploadsTotal)
                    return UploadStartResult.GlobalLimitReached;
                if (_sessions.Values.Count(session =>
                        string.Equals(session.Rfc, rfc, StringComparison.OrdinalIgnoreCase)) >=
                    _storage.MaxConcurrentUploadsPerRfc)
                {
                    return UploadStartResult.RfcLimitReached;
                }

                var added = _sessions.TryAdd(fileUuid, new UploadSessionInfo
                {
                    FileUuid = fileUuid,
                    Rfc = rfc,
                    StartedUtc = DateTimeOffset.UtcNow,
                    BytesReceived = 0,
                    LastUpdateUtc = DateTimeOffset.UtcNow
                });
                return added ? UploadStartResult.Started : UploadStartResult.DuplicateUuid;
            }
        }

        public void Update(string fileUuid, long bytesChunk)
        {
            if (_sessions.TryGetValue(fileUuid, out var s))
            {
                s.BytesReceived += bytesChunk;
                s.LastUpdateUtc = DateTimeOffset.UtcNow;
            }
        }

        public void End(string fileUuid)
        {
            lock (_sessionGate)
            {
                _sessions.TryRemove(fileUuid, out _);
            }

            RemoveWriteState(fileUuid);
        }

        public int ActiveCount()
        {
            var now = DateTimeOffset.UtcNow;
            var c = 0;
            foreach (var s in _sessions.Values)
            {
                if ((now - s.LastUpdateUtc).TotalSeconds < 120)
                    c++;
            }
            return c;
        }

        public int WaitingCount()
        {
            var c = 0;
            foreach (var s in _sessions.Values)
            {
                if (s.BytesReceived == 0)
                    c++;
            }
            return c;
        }

        public IEnumerable<UploadSessionInfo> GetActiveSessions()
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<UploadSessionInfo>();
            foreach (var s in _sessions.Values)
            {
                if ((now - s.LastUpdateUtc).TotalSeconds < 120)
                    list.Add(s);
            }
            return list;
        }

        private readonly ConcurrentDictionary<string, UploadWriteState> _writers =
            new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetWriteState(string fileUuid, out UploadWriteState state) =>
            _writers.TryGetValue(fileUuid, out state!);

        public UploadWriteState GetOrCreateWriteState(
            string fileUuid,
            Func<UploadWriteState> factory)
        {
            if (string.IsNullOrWhiteSpace(fileUuid))
                throw new ArgumentException("fileUuid no puede ser nulo o vacío.", nameof(fileUuid));
            if (factory is null)
                throw new ArgumentNullException(nameof(factory));

            try
            {
                return _writers.GetOrAdd(fileUuid, _ => factory());
            }
            catch
            {
                _writers.TryRemove(fileUuid, out _);
                throw;
            }
        }

        public bool RemoveWriteState(string fileUuid)
        {
            if (_writers.TryRemove(fileUuid, out var state))
            {
                try { state.Dispose(); } catch {  }
                return true;
            }
            return false;
        }

        public int CleanupOrphanWriteStates(TimeSpan idleThreshold)
        {
            var now = DateTimeOffset.UtcNow;
            var removed = 0;

            foreach (var kv in _writers.ToArray())
            {
                if ((now - kv.Value.LastWriteUtc) > idleThreshold)
                {
                    if (RemoveWriteState(kv.Key))
                        removed++;
                }
            }

            return removed;
        }
    }

    public enum UploadStartResult
    {
        Started,
        DuplicateUuid,
        GlobalLimitReached,
        RfcLimitReached
    }

    public sealed class UploadSessionInfo
    {
        public string FileUuid { get; set; } = "";
        public string Rfc { get; set; } = "";
        public long BytesReceived { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset LastUpdateUtc { get; set; }
    }

    public sealed class UploadWriteState : IDisposable
    {
        public string FileUuid { get; }
        public string StagingFilePath { get; }
        public System.IO.FileStream Stream { get; }
        public IncrementalHash? Hash { get; }

        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public bool HashValid { get; private set; } = true;
        public long NextExpectedOffset { get; private set; } = 0;

        public DateTimeOffset LastWriteUtc { get; private set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastFlushUtc { get; private set; } = DateTimeOffset.UtcNow;
        public long BytesSinceFlush { get; private set; } = 0;

        public string? FinalSha256Hex { get; private set; }

        public UploadWriteState(
            string fileUuid,
            string stagingFilePath,
            System.IO.FileStream stream,
            IncrementalHash? hash)
        {
            FileUuid = fileUuid ?? throw new ArgumentNullException(nameof(fileUuid));
            StagingFilePath = stagingFilePath ?? throw new ArgumentNullException(nameof(stagingFilePath));
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Hash = hash;
        }

        public void Touch()
        {
            LastWriteUtc = DateTimeOffset.UtcNow;
        }

        public void AddFlushBytes(int count)
        {
            if (count > 0) BytesSinceFlush += count;
        }

        public void MarkFlushed()
        {
            BytesSinceFlush = 0;
            LastFlushUtc = DateTimeOffset.UtcNow;
        }

        public void InvalidateHash()
        {
            HashValid = false;
            FinalSha256Hex = null; // por seguridad
        }

        public void AdvanceExpectedOffset(long newExpected)
        {
            NextExpectedOffset = newExpected;
        }

        public void SetFinalHash(string hex)
        {
            FinalSha256Hex = hex;
        }

        public void Dispose()
        {
            try { Hash?.Dispose(); } catch {  }
            try { Stream.Dispose(); } catch {  }
            try { WriteGate.Dispose(); } catch {  }
        }
    }
}
