using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using FileXfer.Server.Uploading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length != 1)
{
    Console.Error.WriteLine("Uso: security-smoke <ruta-updater.exe>");
    return 2;
}

var updaterPath = Path.GetFullPath(args[0]);
Require(File.Exists(updaterPath), "No se encontró el updater compilado.");

TestPathProtection();
TestMfaSecretProtection();
TestUploadFileNamePolicy();
TestUploadAdmission();
await TestConcurrentMfaLockoutAsync();
await TestUpdaterAsync(updaterPath);

Console.WriteLine("PASS path-protection=roundtrip randomized tamper-rejected");
Console.WriteLine("PASS mfa-secret-protection=roundtrip randomized tamper-rejected legacy-readable");
Console.WriteLine("PASS upload-filename-policy=canonical dangerous-suffixes-rejected");
Console.WriteLine("PASS upload-admission=pre-message quotas-and-release");
Console.WriteLine("PASS mfa-lockout=concurrent-failures-serialized");
Console.WriteLine("PASS updater=signed-package-applied invalid-signature-rejected zip-slip-rejected");
return 0;

static void TestPathProtection()
{
    var key = RandomNumberGenerator.GetBytes(32);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PathEncryptionKey"] = Convert.ToBase64String(key)
        })
        .Build();
    var protector = new AesGcmPathProtector(configuration);
    const string original = @"C:\Transfer\uploads\RFC123\informe.pdf";

    var encrypted1 = protector.Protect(original);
    var encrypted2 = protector.Protect(original);
    Require(encrypted1.StartsWith("v2:", StringComparison.Ordinal), "El cifrado de ruta no usa el formato v2.");
    Require(encrypted1 != encrypted2, "El cifrado de ruta reutilizó nonce/ciphertext.");
    Require(protector.Unprotect(encrypted1) == original, "La ruta no completó roundtrip AES-GCM.");

    var payload = Convert.FromBase64String(encrypted1[3..]);
    payload[^1] ^= 0x40;
    var tampered = "v2:" + Convert.ToBase64String(payload);
    Expect<CryptographicException>(() => protector.Unprotect(tampered),
        "Una ruta AES-GCM alterada fue aceptada.");
}

static void TestMfaSecretProtection()
{
    var key = RandomNumberGenerator.GetBytes(32);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PathEncryptionKey"] = Convert.ToBase64String(key)
        })
        .Build();
    var protector = new AesGcmMfaSecretProtector(configuration);
    const string original = "JBSWY3DPEHPK3PXP";

    var encrypted1 = protector.Protect(original);
    var encrypted2 = protector.Protect(original);
    Require(protector.IsProtected(encrypted1), "El secreto MFA no usa el formato protegido v2.");
    Require(encrypted1 != encrypted2, "El cifrado MFA reutilizó nonce/ciphertext.");
    Require(protector.Unprotect(encrypted1) == original, "El secreto MFA no completó roundtrip AES-GCM.");
    Require(protector.Unprotect(original) == original, "La migración compatible no aceptó un secreto Base32 legado.");

    var payload = Convert.FromBase64String(encrypted1[3..]);
    payload[^1] ^= 0x20;
    var tampered = "v2:" + Convert.ToBase64String(payload);
    Expect<CryptographicException>(() => protector.Unprotect(tampered),
        "Un secreto MFA AES-GCM alterado fue aceptado.");
}

static void TestUploadFileNamePolicy()
{
    Require(UploadFileNamePolicy.TryValidate("informe-final.pdf", out _),
        "La política rechazó un nombre PDF normal.");
    foreach (var blocked in new[] { "payload.cmd", "payload.cmd ", "payload.ps1.", "PAYLOAD.CmD" })
    {
        Require(!UploadFileNamePolicy.TryValidate(blocked, out _),
            $"La política aceptó un nombre peligroso: '{blocked}'.");
    }
}

static void TestUploadAdmission()
{
    var store = new UploadSessionStore(new StorageOptions
    {
        MaxConcurrentUploadsTotal = 2,
        MaxConcurrentUploadsPerRfc = 1
    });

    Require(store.TryAcquireAdmission("RFCUNO010101AA1", out var first) == UploadAdmissionResult.Started,
        "No se concedió el primer lease de carga.");
    Require(store.TryAcquireAdmission("RFCUNO010101AA1", out _) == UploadAdmissionResult.RfcLimitReached,
        "No se aplicó la cuota por RFC antes del primer mensaje.");
    Require(store.TryAcquireAdmission("RFCDOS010101AA2", out var second) == UploadAdmissionResult.Started,
        "No se concedió el segundo lease global.");
    Require(store.TryAcquireAdmission("RFCTRE010101AA3", out _) == UploadAdmissionResult.GlobalLimitReached,
        "No se aplicó la cuota global antes del primer mensaje.");

    first!.Dispose();
    first.Dispose();
    Require(store.TryAcquireAdmission("RFCUNO010101AA1", out var replacement) == UploadAdmissionResult.Started,
        "El lease no liberó la capacidad por RFC.");
    replacement!.Dispose();
    second!.Dispose();
    Require(store.ActiveAdmissionCount == 0, "Quedaron leases de carga activos después de Dispose.");
}

static async Task TestConcurrentMfaLockoutAsync()
{
    var databaseName = "lockout-smoke-" + Guid.NewGuid().ToString("N");
    var dbOptions = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase(databaseName)
        .Options;
    var factory = new SmokeDbContextFactory(dbOptions);
    var service = new RateLockService(
        factory,
        Options.Create(new RateLimitOptions
        {
            LockoutThreshold = 1_000,
            LockoutMinutes = 30
        }),
        NullLogger<RateLockService>.Instance);

    const string rfc = "LOCK010101AA1";
    const int failures = 50;
    await Task.WhenAll(Enumerable.Range(0, failures).Select(_ => service.RegisterFailureAsync(rfc)));

    await using var db = await factory.CreateDbContextAsync();
    var stored = await db.Lockouts.SingleAsync(item => item.Rfc == rfc);
    Require(stored.FailCount == failures,
        $"Se perdieron fallos MFA concurrentes. Esperado={failures}, real={stored.FailCount}.");
}

static async Task TestUpdaterAsync(string updaterPath)
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "FileXfer.Security.SmokeTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    try
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPath = Path.Combine(root, "update-public.pem");
        await File.WriteAllTextAsync(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
        const string version = "2.1.0";

        var validZip = Path.Combine(root, "valid.zip");
        CreateZip(validZip, archive =>
        {
            AddFile(archive, "SmokeClient.exe", GetHarmlessWindowsExecutable());
            AddText(archive, "config.txt", "new");
        });
        var validManifest = Sign(validZip, version, rsa);
        var validDestination = Path.Combine(root, "valid-destination");
        Directory.CreateDirectory(validDestination);
        await File.WriteAllTextAsync(Path.Combine(validDestination, "config.txt"), "old");

        var validResult = await RunUpdaterAsync(
            updaterPath,
            validZip,
            validDestination,
            "SmokeClient.exe",
            version,
            validManifest.Hash,
            validManifest.Signature,
            publicKeyPath);
        Require(validResult.ExitCode == 0,
            $"El updater rechazó un paquete firmado: {validResult.Output}");
        Require(File.Exists(Path.Combine(validDestination, "SmokeClient.exe")),
            "El updater no instaló el ejecutable validado.");
        Require(await File.ReadAllTextAsync(Path.Combine(validDestination, "config.txt")) == "new",
            "El updater no aplicó el contenido del paquete validado.");

        var rejectedDestination = Path.Combine(root, "rejected-destination");
        Directory.CreateDirectory(rejectedDestination);
        var sentinel = Path.Combine(rejectedDestination, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        var badSignature = validManifest.Signature.ToArray();
        badSignature[^1] ^= 0x01;

        var rejectedResult = await RunUpdaterAsync(
            updaterPath,
            validZip,
            rejectedDestination,
            "SmokeClient.exe",
            version,
            validManifest.Hash,
            badSignature,
            publicKeyPath);
        Require(rejectedResult.ExitCode == 2, "El updater no rechazó una firma alterada.");
        Require(await File.ReadAllTextAsync(sentinel) == "untouched" &&
                !File.Exists(Path.Combine(rejectedDestination, "SmokeClient.exe")),
            "El updater modificó el destino antes de validar la firma.");

        var traversalZip = Path.Combine(root, "traversal.zip");
        CreateZip(traversalZip, archive =>
        {
            AddText(archive, "../escape.txt", "blocked");
            AddFile(archive, "SmokeClient.exe", GetHarmlessWindowsExecutable());
        });
        var traversalManifest = Sign(traversalZip, version, rsa);
        var traversalDestination = Path.Combine(root, "traversal-destination");
        Directory.CreateDirectory(traversalDestination);

        var traversalResult = await RunUpdaterAsync(
            updaterPath,
            traversalZip,
            traversalDestination,
            "SmokeClient.exe",
            version,
            traversalManifest.Hash,
            traversalManifest.Signature,
            publicKeyPath);
        Require(traversalResult.ExitCode == 2, "El updater no rechazó un ZIP con path traversal.");
        Require(!File.Exists(Path.Combine(traversalDestination, "SmokeClient.exe")),
            "El updater comenzó a aplicar un ZIP con path traversal.");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

static (string Hash, byte[] Signature) Sign(string zipPath, string version, RSA rsa)
{
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
    var canonical = string.Join("\n", new[]
    {
        "FILEXFER-UPDATE-V1",
        "transfer-cliente-ase",
        version,
        hash
    });
    var signature = rsa.SignData(
        Encoding.UTF8.GetBytes(canonical),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pss);
    return (hash, signature);
}

static void CreateZip(string path, Action<ZipArchive> createEntries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    createEntries(archive);
}

static void AddText(ZipArchive archive, string entryName, string content)
{
    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
    writer.Write(content);
}

static void AddFile(ZipArchive archive, string entryName, string sourcePath)
{
    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
    using var input = File.OpenRead(sourcePath);
    using var output = entry.Open();
    input.CopyTo(output);
}

static string GetHarmlessWindowsExecutable()
{
    var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
    var path = Path.Combine(systemDirectory, "where.exe");
    Require(File.Exists(path), "No se encontró where.exe para la prueba aislada del updater.");
    return path;
}

static async Task<(int ExitCode, string Output)> RunUpdaterAsync(
    string updaterPath,
    string zipPath,
    string destination,
    string exeName,
    string version,
    string hash,
    byte[] signature,
    string publicKeyPath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = updaterPath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add(zipPath);
    startInfo.ArgumentList.Add(destination);
    startInfo.ArgumentList.Add(exeName);
    startInfo.ArgumentList.Add(version);
    startInfo.ArgumentList.Add(hash);
    startInfo.ArgumentList.Add(Convert.ToBase64String(signature));
    startInfo.ArgumentList.Add(publicKeyPath);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("No se pudo iniciar el updater de prueba.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await process.WaitForExitAsync(timeout.Token);
    return (process.ExitCode, (await stdout) + (await stderr));
}

static void Expect<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class SmokeDbContextFactory(DbContextOptions<FileXferDbContext> options)
    : IDbContextFactory<FileXferDbContext>
{
    public FileXferDbContext CreateDbContext() => new(options);

    public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileXferDbContext(options));
}
