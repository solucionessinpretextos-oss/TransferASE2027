using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Options;

namespace FileXfer.Server.Services;

public sealed record StorageCapacity(long TotalBytes, long FreeBytes);

public interface IStorageCapacityReader
{
    StorageCapacity Read(string path);
}

/// <summary>
/// Verifica la operación real con un archivo propio de 32 bytes. La capacidad
/// es telemetría independiente: no convierte una ruta UNC utilizable en una falla.
/// </summary>
public sealed class StorageHealthProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly StorageOptions _storage;
    private readonly ILogger<StorageHealthProbe> _logger;
    private readonly IStorageCapacityReader _capacityReader;
    private readonly object _gate = new();
    private Task<StorageHealthDiagnosticDto>? _inFlight;
    private Task<StorageCapacity>? _capacityInFlight;
    private StorageHealthDiagnosticDto? _cached;

    public StorageHealthProbe(
        StorageOptions storage,
        ILogger<StorageHealthProbe> logger,
        IStorageCapacityReader? capacityReader = null)
    {
        _storage = storage;
        _logger = logger;
        _capacityReader = capacityReader ?? new NativeStorageCapacityReader();
    }

    public async Task<StorageHealthDiagnosticDto> GetSnapshotAsync(bool forceRefresh, CancellationToken ct)
    {
        Task<StorageHealthDiagnosticDto> probe;
        lock (_gate)
        {
            if (_inFlight is { IsCompletedSuccessfully: true })
                _cached = _inFlight.Result;

            if (!forceRefresh && _cached is not null && DateTimeOffset.UtcNow - _cached.CheckedUtc < CacheDuration)
                return _cached;

            // Una llamada de red bloqueada por el SO no genera más probes por cada
            // refresco. El mismo trabajo continúa y limpia su propio archivo al terminar.
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = Task.Run(RunProbeAsync);
            probe = _inFlight;
        }

        try
        {
            var result = await probe.WaitAsync(
                forceRefresh ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(1), ct);
            lock (_gate) _cached = result;
            return result;
        }
        catch (TimeoutException)
        {
            return Diagnostic("StorageProbeTimedOut",
                "La comprobación de lectura, escritura y borrado todavía no responde dentro del tiempo permitido.",
                "Compruebe red/SMB y disponibilidad del servidor de archivos. Revalidar comprueba la misma operación bajo la identidad del servicio; no cambia permisos ni elimina documentos.");
        }
    }

    private async Task<StorageHealthDiagnosticDto> RunProbeAsync()
    {
        var diagnostic = Diagnostic("StorageChecking", "Comprobando almacenamiento.", "Espere la conclusión de la prueba funcional.");
        if (string.IsNullOrWhiteSpace(_storage.BasePath))
            return Diagnostic("StoragePathNotConfigured", "La ruta de almacenamiento no está configurada.",
                "Configure Storage:BasePath con la ruta compartida usada por el servicio y vuelva a comprobar.");

        string fullPath;
        try { fullPath = Path.GetFullPath(_storage.BasePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Diagnostic("StoragePathInvalid", $"La ruta configurada no es válida ({ex.GetType().Name}).",
                "Corrija Storage:BasePath y vuelva a comprobar la ruta con la cuenta del servicio.");
        }

        diagnostic.Path = fullPath;
        string? probePath = null;
        var created = false;
        var phase = "acceso";
        try
        {
            // Directory.Exists puede devolver false también por falta de permisos;
            // el mensaje no atribuye una causa que no se haya demostrado.
            if (!Directory.Exists(fullPath))
            {
                diagnostic.Code = "StorageDirectoryUnavailable";
                diagnostic.Message = "La carpeta configurada no existe o no es accesible para la cuenta del servicio.";
                diagnostic.Remediation = $"Verifique que la ruta compartida exista, que SMB responda y que {diagnostic.ServiceIdentity} tenga acceso a la carpeta. Después pulse Revalidar almacenamiento.";
                return diagnostic;
            }

            var payload = RandomNumberGenerator.GetBytes(32);
            probePath = Path.Combine(fullPath, ".filexfer-health-" + Guid.NewGuid().ToString("N") + ".tmp");
            phase = "escritura";
            await using (var writer = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await writer.WriteAsync(payload);
                await writer.FlushAsync();
            }
            diagnostic.CanWrite = true;

            phase = "lectura";
            var observed = new byte[payload.Length];
            await using (var reader = new FileStream(probePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                await reader.ReadExactlyAsync(observed);
                if (reader.Length != payload.Length || !CryptographicOperations.FixedTimeEquals(payload, observed))
                    throw new InvalidDataException("La prueba de integridad de almacenamiento no coincidió.");
            }
            diagnostic.CanRead = true;

            phase = "borrado";
            File.Delete(probePath);
            created = false;
            diagnostic.CanDelete = true;

            // La consulta de capacidad tiene su propio tiempo acotado y su fallo
            // no cambia el resultado de las tres operaciones demostradas arriba.
            try
            {
                Task<StorageCapacity> capacityTask;
                lock (_gate)
                {
                    if (_capacityInFlight is null || _capacityInFlight.IsCompleted)
                        _capacityInFlight = Task.Run(() => _capacityReader.Read(fullPath));
                    capacityTask = _capacityInFlight;
                }
                var capacity = await capacityTask.WaitAsync(TimeSpan.FromMilliseconds(250));
                if (capacity.TotalBytes <= 0 || capacity.FreeBytes < 0 || capacity.FreeBytes > capacity.TotalBytes)
                    throw new InvalidDataException("La capacidad informada no es válida.");
                diagnostic.CapacityAvailable = true;
                diagnostic.TotalBytes = capacity.TotalBytes;
                diagnostic.FreeBytes = capacity.FreeBytes;
                diagnostic.Code = "StorageReady";
                diagnostic.Message = "Lectura, escritura y borrado confirmados con un archivo de prueba de 32 bytes; capacidad disponible.";
                diagnostic.Remediation = "No requiere intervención de almacenamiento. Revalidar vuelve a ejecutar esta prueba bajo la identidad del servicio.";
            }
            catch (Exception ex)
            {
                diagnostic.Code = "StorageReadyCapacityUnknown";
                diagnostic.Message = $"Lectura, escritura y borrado confirmados. La capacidad del volumen no pudo medirse ({ex.GetType().Name}); esto no impide transferir archivos.";
                diagnostic.Remediation = "El almacenamiento está operativo. Consulte la cuota/capacidad en el servidor de archivos si necesita conocer el espacio libre y pulse Revalidar para repetir la medición.";
                _logger.LogDebug(ex, "No se obtuvo capacidad para la ruta de almacenamiento comprobada.");
            }
        }
        catch (Exception ex)
        {
            diagnostic.Code = phase switch
            {
                "escritura" => ex is UnauthorizedAccessException ? "StorageWriteDenied" : "StorageWriteFailed",
                "lectura" => ex is UnauthorizedAccessException ? "StorageReadDenied" : "StorageReadFailed",
                "borrado" => ex is UnauthorizedAccessException ? "StorageDeleteDenied" : "StorageDeleteFailed",
                _ => "StorageAccessFailed"
            };
            diagnostic.Message = $"Falló la operación de {phase} de la prueba de 32 bytes. Tipo: {ex.GetType().Name}; código del sistema: {ex.HResult & 0xFFFF}.";
            diagnostic.Remediation = $"Compruebe acceso SMB, espacio/cuota y permisos de {phase} para {diagnostic.ServiceIdentity} en la ruta indicada; revise ACL de carpeta y recurso compartido. Después pulse Revalidar almacenamiento.";
            _logger.LogWarning(ex, "Prueba funcional de almacenamiento falló en {Phase}.", phase);
        }
        finally
        {
            // Nunca se elimina un archivo que no haya sido creado por este probe.
            if (created && probePath is not null)
            {
                try { File.Delete(probePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "No se pudo retirar el archivo propio de prueba de almacenamiento."); }
            }
            diagnostic.CheckedUtc = DateTimeOffset.UtcNow;
        }
        return diagnostic;
    }

    private StorageHealthDiagnosticDto Diagnostic(string code, string message, string remediation) => new()
    {
        Code = code,
        Message = message,
        Remediation = remediation,
        Path = _storage.BasePath ?? string.Empty,
        ServiceIdentity = GetServiceIdentity(),
        CheckedUtc = DateTimeOffset.UtcNow
    };

    private static string GetServiceIdentity()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.Name;
            }
            return Environment.UserName;
        }
        catch { return "Identidad del servicio no disponible"; }
    }

    private sealed class NativeStorageCapacityReader : IStorageCapacityReader
    {
        public StorageCapacity Read(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("La medición UNC requiere Windows.");
                if (!GetDiskFreeSpaceExW(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                    out var available, out var total, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return new StorageCapacity(checked((long)total), checked((long)available));
            }
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return new StorageCapacity(drive.TotalSize, drive.AvailableFreeSpace);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
