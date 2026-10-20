using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Uploading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var root = Path.Combine(Path.GetTempPath(), "FileXfer-ResumeSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var options = new StorageOptions
    {
        BasePath = root,
        MaxUploadBytes = 1024 * 1024,
        MaxChunkBytes = 1024 * 1024,
        MaxConcurrentUploadsTotal = 2,
        MaxConcurrentUploadsPerRfc = 1,
        FirstChunkTimeoutSeconds = 30,
        IdleChunkTimeoutSeconds = 30,
        MaxUploadDurationMinutes = 5,
        ResumeRetentionHours = 72
    };
    var sessions = new UploadSessionStore(options);
    using var loggerFactory = LoggerFactory.Create(builder => { });
    var assembler = new FileAssembler(
        options,
        new TestDbContextFactory(),
        loggerFactory.CreateLogger<FileAssembler>(),
        sessions);

    var uuid = Guid.NewGuid().ToString("N");
    const string storageKey = "10/20/30/40/50";
    const string fileName = "checkpoint.bin";
    Require(sessions.TryBegin(uuid, "TST010101AAA") == UploadStartResult.Started, "No inició la sesión de prueba.");

    var confirmed = new byte[] { 1, 2, 3, 4, 5, 6 };
    await assembler.AppendAsync(storageKey, uuid, fileName, 0, confirmed, CancellationToken.None);
    var committedLength = await assembler.FlushToDiskAsync(uuid, CancellationToken.None);
    Require(committedLength == confirmed.Length, "El checkpoint no confirmó la longitud esperada.");

    var uncommitted = new byte[] { 7, 8, 9, 10 };
    await assembler.AppendAsync(storageKey, uuid, fileName, confirmed.Length, uncommitted, CancellationToken.None);

    // Simula el cierre abrupto del proceso: el sistema operativo puede haber alcanzado a
    // escribir una cola que SQL todavía no confirmó.
    sessions.End(uuid);
    var physicalBeforeRecovery = assembler.GetPersistedLength(storageKey, uuid, fileName);
    Require(physicalBeforeRecovery == confirmed.Length + uncommitted.Length, "No se creó la cola sin checkpoint para la prueba.");

    var recoveredLength = assembler.ReconcileToCommittedLength(
        storageKey, uuid, fileName, committedLength);
    Require(recoveredLength == committedLength, "La recuperación no respetó el offset confirmado.");

    var path = assembler.ResolveAbsolutePath(storageKey, uuid, fileName);
    var recovered = await File.ReadAllBytesAsync(path);
    Require(recovered.SequenceEqual(confirmed), "La recuperación conservó bytes no confirmados o dañó el prefijo válido.");

    Console.WriteLine("PASS: server truncates uncommitted tail and resumes from durable checkpoint.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class TestDbContextFactory : IDbContextFactory<FileXferDbContext>
{
    private readonly DbContextOptions<FileXferDbContext> _options =
        new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("resume-smoke-" + Guid.NewGuid().ToString("N"))
            .Options;

    public FileXferDbContext CreateDbContext() => new(_options);

    public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
