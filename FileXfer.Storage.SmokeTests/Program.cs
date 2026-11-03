using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 2 && args[0] == "--path")
{
    var actualProbe = new StorageHealthProbe(new StorageOptions { BasePath = args[1] },
        NullLogger<StorageHealthProbe>.Instance);
    var actual = await actualProbe.GetSnapshotAsync(true, default);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        actual.Code, actual.CanRead, actual.CanWrite, actual.CanDelete,
        actual.CapacityAvailable, actual.TotalBytes, actual.FreeBytes,
        actual.ServiceIdentity, actual.CheckedUtc
    }));
    return actual.CanRead && actual.CanWrite && actual.CanDelete ? 0 : 1;
}

var root = Path.Combine(Path.GetTempPath(), "FileXfer-StorageSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sentinel = Path.Combine(root, "user-document.txt");
    await File.WriteAllTextAsync(sentinel, "original-document");
    var capacity = new CountingCapacityReader();
    var storage = new StorageOptions { BasePath = root };
    var probe = new StorageHealthProbe(storage, NullLogger<StorageHealthProbe>.Instance, capacity);
    var first = await probe.GetSnapshotAsync(true, default);
    Require(first.Code == "StorageReady" && first.CanRead && first.CanWrite && first.CanDelete,
        "La prueba funcional local no confirmó las tres operaciones.");
    Require(first.CapacityAvailable && first.TotalBytes == 1000 && first.FreeBytes == 400,
        "La capacidad comprobada no se incluyó en el diagnóstico.");
    Require(!string.IsNullOrWhiteSpace(first.ServiceIdentity) && first.Path == root && first.CheckedUtc > DateTimeOffset.UtcNow.AddMinutes(-1),
        "El diagnóstico no informa identidad, ruta y hora reales.");
    Require(Directory.GetFiles(root).SequenceEqual(new[] { sentinel }) && await File.ReadAllTextAsync(sentinel) == "original-document",
        "La prueba dejó un archivo temporal o modificó el documento ajeno.");
    var cached = await probe.GetSnapshotAsync(false, default);
    Require(ReferenceEquals(first, cached) && capacity.Calls == 1,
        "El refresco ordinario no reutilizó la medición de 30 segundos.");
    await probe.GetSnapshotAsync(true, default);
    Require(capacity.Calls == 2, "Revalidar no forzó una nueva medición.");

    var nativeProbe = new StorageHealthProbe(storage, NullLogger<StorageHealthProbe>.Instance);
    var nativeSnapshot = await nativeProbe.GetSnapshotAsync(true, default);
    Require(nativeSnapshot.CanRead && nativeSnapshot.CanWrite && nativeSnapshot.CanDelete &&
            nativeSnapshot.CapacityAvailable && nativeSnapshot.TotalBytes is > 0 && nativeSnapshot.FreeBytes is >= 0,
        "El lector nativo de capacidad local no obtuvo el espacio disponible del volumen real.");

    var unmeasuredProbe = new StorageHealthProbe(storage, NullLogger<StorageHealthProbe>.Instance, new FailingCapacityReader());
    var unmeasured = await unmeasuredProbe.GetSnapshotAsync(true, default);
    Require(unmeasured.Code == "StorageReadyCapacityUnknown" && !unmeasured.CapacityAvailable &&
            unmeasured.CanRead && unmeasured.CanWrite && unmeasured.CanDelete,
        "Una falla al medir capacidad se confundió con almacenamiento no operativo.");

    var factory = new TestDbFactory(new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("storage-health-" + Guid.NewGuid().ToString("N")).Options);
    var health = new SystemHealthService(factory, storage, new AlertState(), NullLogger<SystemHealthService>.Instance, unmeasuredProbe);
    var snapshot = await health.GetSnapshotAsync(default);
    Require(snapshot.StorageOk && snapshot.IsHealthy && !snapshot.DiskCapacityAvailable &&
            snapshot.StorageDiagnostic?.Code == "StorageReadyCapacityUnknown",
        "La salud del sistema penalizó indebidamente la telemetría de capacidad desconocida.");

    var missing = new StorageHealthProbe(new StorageOptions { BasePath = Path.Combine(root, "not-created") },
        NullLogger<StorageHealthProbe>.Instance, capacity);
    var unavailable = await missing.GetSnapshotAsync(true, default);
    Require(unavailable.Code == "StorageDirectoryUnavailable" && !unavailable.CanRead && !unavailable.CanWrite && !unavailable.CanDelete &&
            !Directory.Exists(Path.Combine(root, "not-created")),
        "Una carpeta inexistente se marcó operativa o se creó silenciosamente.");
    var missingHealth = new SystemHealthService(factory, storage, new AlertState(), NullLogger<SystemHealthService>.Instance, missing);
    var missingSnapshot = await missingHealth.RecheckAsync(default);
    Require(!missingSnapshot.StorageOk && !missingSnapshot.IsHealthy && missingSnapshot.Message!.Contains("no existe o no es accesible"),
        "Una falla real de acceso no degrada la salud o no muestra su causa concreta.");

    var invalidDirectory = new StorageHealthProbe(new StorageOptions { BasePath = sentinel },
        NullLogger<StorageHealthProbe>.Instance, capacity);
    Require((await invalidDirectory.GetSnapshotAsync(true, default)).Code == "StorageDirectoryUnavailable",
        "Se aceptó como carpeta una ruta que es un archivo.");
    Require(await File.ReadAllTextAsync(sentinel) == "original-document", "El probe modificó el archivo de usuario.");
    Console.WriteLine("PASS storage functional=write-read-delete capacity=independent cache=30-seconds recheck=forced missing-path=unhealthy diagnostics=actionable own-probe=cleaned documents=unchanged");
    return 0;
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "FileXfer-StorageSmoke-", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Directorio temporal fuera del ámbito de la prueba.");
    Directory.Delete(resolved, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class CountingCapacityReader : IStorageCapacityReader
{
    public int Calls;
    public StorageCapacity Read(string path) { Interlocked.Increment(ref Calls); return new(1000, 400); }
}

file sealed class FailingCapacityReader : IStorageCapacityReader
{
    public StorageCapacity Read(string path) => throw new ArgumentException("Simula DriveInfo sin soporte UNC.");
}

file sealed class TestDbFactory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
{
    public FileXferDbContext CreateDbContext() => new(options);
    public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
}

file sealed class AlertState : IAlertDeliveryState
{
    public AlertDeliverySnapshot GetSnapshot() => new(false, true, 0, 0, 0, 100, 0, 0, 0, 0, 0, 0, null, null, null);
}
