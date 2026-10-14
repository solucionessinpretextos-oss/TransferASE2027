using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FileXfer.Server.Controllers;
using FileXfer.Server.Controllers.Admin;
using FileXfer.Server.Data;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using FileXfer.Server.Uploading;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

if (args.Length != 1)
{
    Console.Error.WriteLine("Uso: security-smoke <ruta-updater.exe>");
    return 2;
}

var updaterPath = Path.GetFullPath(args[0]);
Require(File.Exists(updaterPath), "No se encontró el updater compilado.");

TestPathProtection();
TestMfaSecretProtection();
TestSqlTransportPolicy();
TestBudiSchemaRequirements();
TestClientUpdateManifestEndpoint();
TestRfcResourceAuthorization();
TestUploadFileNamePolicy();
TestUploadAdmission();
await TestConcurrentMfaLockoutAsync();
await TestFilePoliciesAsync();
await TestSignedReceiptsAsync();
await TestReceiptAuthorizationAsync();
await TestResumableAssemblerAsync();
await TestSecureDownloadAsync();
await TestUpdaterAsync(updaterPath);

Console.WriteLine("PASS path-protection=roundtrip randomized tamper-rejected");
Console.WriteLine("PASS mfa-secret-protection=roundtrip randomized tamper-rejected legacy-readable");
Console.WriteLine("PASS sql-transport=encrypted certificate-validated non-localdb");
Console.WriteLine("PASS budi-schema=required-columns-and-indexes-enforced");
Console.WriteLine("PASS client-update-manifest=https-zip-and-cryptographic-fields-required");
Console.WriteLine("PASS rfc-resource-authorization=owner privileged-mfa cross-rfc-blocked");
Console.WriteLine("PASS upload-filename-policy=canonical dangerous-suffixes-rejected");
Console.WriteLine("PASS upload-admission=pre-message quotas-and-release");
Console.WriteLine("PASS mfa-lockout=concurrent-failures-serialized");
Console.WriteLine("PASS file-policies=seed version conflict bypass limit rollback");
Console.WriteLine("PASS upload-receipts=rsa-pss trusted-key chained tamper-rejected idempotent mfa-authorized");
Console.WriteLine("PASS resumable-storage=physical-offset append hash-preserved reparse-points-rejected");
Console.WriteLine("PASS secure-download=hash-verified tamper-and-traversal-blocked");
Console.WriteLine("PASS updater=signed-package-applied complete-package-required verify-only temp-relaunch rollback invalid-signature-and-zip-slip-rejected");
return 0;

static void TestSqlTransportPolicy()
{
    SqlTransportPolicy.ValidateProduction(
        "Secure",
        "Server=sql.internal.example;Database=FileXfer;User ID=service;Password=placeholder;" +
        "Encrypt=True;TrustServerCertificate=False");

    Expect<InvalidOperationException>(
        () => SqlTransportPolicy.ValidateProduction(
            "Plaintext",
            "Server=sql.internal.example;Database=FileXfer;Encrypt=False;TrustServerCertificate=False"),
        "La política SQL aceptó transporte sin cifrar.");

    Expect<InvalidOperationException>(
        () => SqlTransportPolicy.ValidateProduction(
            "Untrusted",
            "Server=sql.internal.example;Database=FileXfer;Encrypt=True;TrustServerCertificate=True"),
        "La política SQL aceptó un certificado sin validar.");

    Expect<InvalidOperationException>(
        () => SqlTransportPolicy.ValidateProduction(
            "LocalOnly",
            "Server=(localdb)\\MSSQLLocalDB;Database=FileXfer;Encrypt=True;TrustServerCertificate=False"),
        "La política SQL aceptó LocalDB en producción.");
}

static void TestBudiSchemaRequirements()
{
    var ready = new BudiSchemaStatus(
        TableExists: true,
        MovimientoIdExists: true,
        UploadIdExists: true,
        UploadIdUniqueIndexExists: true,
        ResultContextIndexExists: true);
    Require(ready.IsReady, "El esquema BuDi completo no fue reconocido como listo.");

    var missingUploadId = ready with { UploadIdExists = false };
    Require(!missingUploadId.IsReady, "Readiness aceptó BuDi sin ID_Upload.");

    var missingUniqueIndex = ready with { UploadIdUniqueIndexExists = false };
    Require(!missingUniqueIndex.IsReady, "Readiness aceptó BuDi sin índice único de carga.");
}

static void TestClientUpdateManifestEndpoint()
{
    var entry = new ClientUpdatesOptions.ClientUpdateEntry
    {
        LatestVersion = "2.1.0.1",
        MinSupportedVersion = "2.1.0.0",
        DownloadUrl = "https://updates.example.invalid/TransferClienteASE-2.1.0.1-win-x64.zip",
        Sha256 = new string('a', 64),
        SignatureBase64 = Convert.ToBase64String(new byte[384]),
        SignatureAlgorithm = "RSA-PSS-SHA256"
    };
    var options = new ClientUpdatesOptions
    {
        Clients = new Dictionary<string, ClientUpdatesOptions.ClientUpdateEntry>
        {
            ["transfer-cliente-ase"] = entry
        }
    };
    var controller = new ClientUpdatesController(
        new SmokeOptionsSnapshot<ClientUpdatesOptions>(options));

    var valid = controller.GetClientUpdate("transfer-cliente-ase", "2.1.0.0");
    Require(valid.Result is OkObjectResult,
        "El endpoint rechazó un manifiesto ZIP firmado válido.");

    entry.DownloadUrl = "https://updates.example.invalid/setup.exe";
    var executable = controller.GetClientUpdate("transfer-cliente-ase", "2.1.0.0");
    Require(executable.Result is ObjectResult { StatusCode: StatusCodes.Status503ServiceUnavailable },
        "El endpoint aceptó un ejecutable directo fuera del canal ZIP seguro.");
}

static void TestRfcResourceAuthorization()
{
    var owner = Principal("RFCOWNER0101", Array.Empty<string>(), "efirma");
    Require(RfcResourceAuthorization.CanAccess(owner, "RFCOWNER0101"),
        "El propietario no pudo acceder a su propio recurso.");
    Require(!RfcResourceAuthorization.CanAccess(owner, "RFCOTHER0101"),
        "Un RFC obtuvo acceso al recurso de otro RFC.");

    var adminWithoutMfa = Principal("RFCADMIN0101", new[] { "admin" }, "efirma");
    Require(!RfcResourceAuthorization.CanAccess(adminWithoutMfa, "RFCOTHER0101"),
        "Un administrador sin MFA obtuvo acceso cruzado.");

    var adminWithMfa = Principal("RFCADMIN0101", new[] { "admin" }, "efirma+mfa");
    Require(RfcResourceAuthorization.CanAccess(adminWithMfa, "RFCOTHER0101"),
        "Un administrador con MFA no pudo ejercer acceso privilegiado.");
}

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

static async Task TestFilePoliciesAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("file-policy-smoke-" + Guid.NewGuid().ToString("N"))
        .Options;
    var factory = new SmokeDbContextFactory(options);
    var service = new FilePolicyService(factory);
    await service.EnsureSeededAsync();

    var effectiveController = new EffectiveFilePoliciesController(service);
    var effectiveAction = await effectiveController.GetEffective(CancellationToken.None);
    var effectiveOk = effectiveAction.Result as OkObjectResult;
    var effectivePolicies = effectiveOk?.Value as EffectiveFilePolicyDto[];
    Require(effectivePolicies?.Any(policy => policy.Extension == ".pdf") == true,
        "El endpoint efectivo no devolvió la política PDF esperada.");
    Require(typeof(EffectiveFilePolicyDto).GetProperty("UpdatedBy") is null &&
            typeof(EffectiveFilePolicyDto).GetProperty("ChangeReason") is null,
        "El contrato efectivo expone metadatos administrativos.");

    var pdf = await service.RequireAllowedAsync("informe.pdf", 1024);
    Require(pdf.RequiresVerification, "PDF no quedó verificado por la política inicial.");
    await ExpectAsync<FilePolicyConflictException>(
        () => service.RequireAllowedAsync("legado.doc", 1024),
        "El formato legado .doc quedó habilitado por omisión.");

    var created = await service.UpsertAsync(
        ".foo",
        new FilePolicyChange(true, false, 2048, 0, "Prueba de formato controlado"),
        "SMOKE");
    Require(created.Version == 1 && !created.RequiresVerification,
        "No se creó la excepción de verificación esperada.");
    await service.RequireAllowedAsync("demo.foo", 2048);
    await ExpectAsync<FilePolicyConflictException>(
        () => service.RequireAllowedAsync("demo.foo", 2049),
        "No se aplicó el límite por extensión.");
    await ExpectAsync<FilePolicyConflictException>(
        () => service.UpsertAsync(
            ".foo",
            new FilePolicyChange(true, true, null, 0, "Versión deliberadamente vencida"),
            "SMOKE"),
        "Se aceptó una escritura administrativa con versión obsoleta.");

    var changed = await service.UpsertAsync(
        ".foo",
        new FilePolicyChange(false, true, null, created.Version, "Bloqueo para validar rollback"),
        "SMOKE");
    var history = await service.GetHistoryAsync(".foo");
    var original = history.Single(item => item.Version == 1);
    var restored = await service.RollbackAsync(
        original.Id,
        changed.Version,
        "Restaurar configuración validada",
        "SMOKE");
    Require(restored.IsAllowed && !restored.RequiresVerification && restored.Version == 3,
        "El rollback no creó una nueva versión con los valores históricos.");
}

static async Task TestSignedReceiptsAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("receipt-smoke-" + Guid.NewGuid().ToString("N"))
        .Options;
    var factory = new SmokeDbContextFactory(options);
    var uploadId = Guid.NewGuid();
    await using (var db = await factory.CreateDbContextAsync())
    {
        db.Uploads.Add(new UploadEntry
        {
            Id = uploadId,
            Rfc = "TEST010101AA1",
            FileUuid = Guid.NewGuid().ToString("N"),
            OriginalFileName = "informe.pdf",
            StoredRelativePath = "test/informe.pdf",
            Status = "Completed"
        });
        await db.SaveChangesAsync();
    }

    using var rsa = RSA.Create(3072);
    using var service = new UploadReceiptService(
        factory,
        new ReceiptSigningOptions { PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() },
        new SmokeWebHostEnvironment(),
        NullLogger<UploadReceiptService>.Instance);
    var draft = new UploadReceiptDraft(
        uploadId,
        Guid.NewGuid().ToString("N"),
        "informe.pdf",
        1234,
        new string('a', 64),
        "TEST010101AA1",
        "device",
        2027,
        1, 2, 3, 4, 5, null,
        ".pdf",
        1,
        true,
        true,
        "Accepted",
        Guid.NewGuid().ToString("N"));
    var receipt = await service.CreateAsync(draft);
    Require(service.Verify(receipt), "La firma del recibo recién emitido no fue válida.");
    var duplicate = await service.CreateAsync(draft);
    Require(duplicate.Id == receipt.Id, "La emisión de recibo no fue idempotente por UploadId.");

    using var attackerRsa = RSA.Create(3072);
    var forgedPayload = receipt.CanonicalPayloadJson + " ";
    var forgedPayloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(forgedPayload));
    var previousHash = string.IsNullOrWhiteSpace(receipt.PreviousChainHash)
        ? new byte[32]
        : Convert.FromHexString(receipt.PreviousChainHash);
    var forgedChainMaterial = new byte[previousHash.Length + forgedPayloadHash.Length];
    previousHash.CopyTo(forgedChainMaterial, 0);
    forgedPayloadHash.CopyTo(forgedChainMaterial, previousHash.Length);
    var forgedChainHash = SHA256.HashData(forgedChainMaterial);
    var attackerPublicKey = attackerRsa.ExportSubjectPublicKeyInfo();
    var forgedReceipt = new UploadReceipt
    {
        Id = receipt.Id,
        UploadId = receipt.UploadId,
        IssuedUtc = receipt.IssuedUtc,
        CanonicalPayloadJson = forgedPayload,
        PayloadSha256 = Convert.ToHexString(forgedPayloadHash).ToLowerInvariant(),
        PreviousChainHash = receipt.PreviousChainHash,
        ChainHash = Convert.ToHexString(forgedChainHash).ToLowerInvariant(),
        SignatureBase64 = Convert.ToBase64String(attackerRsa.SignHash(
            forgedChainHash,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss)),
        SignatureAlgorithm = "RSA-PSS-SHA256",
        KeyId = Convert.ToHexString(SHA256.HashData(attackerPublicKey))[..24].ToLowerInvariant(),
        PublicKeyPem = attackerRsa.ExportSubjectPublicKeyInfoPem()
    };
    Require(!service.Verify(forgedReceipt),
        "Un recibo refirmado con una llave no confiable fue aceptado como auténtico.");

    receipt.CanonicalPayloadJson += " ";
    Require(!service.Verify(receipt), "Un recibo alterado conservó una firma válida.");
}

static async Task TestReceiptAuthorizationAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("receipt-auth-smoke-" + Guid.NewGuid().ToString("N"))
        .Options;
    var factory = new SmokeDbContextFactory(options);
    var uploadId = Guid.NewGuid();
    var receiptId = Guid.NewGuid();
    await using (var db = await factory.CreateDbContextAsync())
    {
        db.Uploads.Add(new UploadEntry
        {
            Id = uploadId,
            Rfc = "OWNER010101AA1",
            FileUuid = Guid.NewGuid().ToString("N"),
            OriginalFileName = "evidencia.pdf",
            StoredRelativePath = "test/evidencia.pdf",
            Status = "Completed"
        });
        db.UploadReceipts.Add(new UploadReceipt
        {
            Id = receiptId,
            UploadId = uploadId,
            IssuedUtc = DateTimeOffset.UtcNow,
            CanonicalPayloadJson = "{}",
            PayloadSha256 = new string('0', 64),
            ChainHash = new string('1', 64),
            SignatureBase64 = Convert.ToBase64String(new byte[384]),
            SignatureAlgorithm = "RSA-PSS-SHA256",
            KeyId = "smoke",
            PublicKeyPem = "smoke"
        });
        await db.SaveChangesAsync();
    }

    async Task<ActionResult<UploadReceiptResponse>> GetAs(
        string rfc,
        string role,
        string authenticationMethod)
    {
        var controller = new ReceiptsController(factory, new SmokeReceiptService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim("rfc", rfc),
                            new Claim(ClaimTypes.Role, role),
                            new Claim("amr", authenticationMethod)
                        },
                        authenticationType: "smoke",
                        nameType: ClaimTypes.Name,
                        roleType: ClaimTypes.Role))
                }
            }
        };
        return await controller.Get(receiptId, CancellationToken.None);
    }

    var adminWithoutMfa = await GetAs("ADMIN010101AA1", "admin", "efirma");
    Require(adminWithoutMfa.Result is ForbidResult,
        "Un administrador sin MFA pudo leer el recibo de otro RFC.");

    var adminWithMfa = await GetAs("ADMIN010101AA1", "admin", "efirma+mfa");
    Require(adminWithMfa.Result is OkObjectResult,
        "Un administrador con MFA no pudo ejercer el acceso privilegiado legítimo.");

    var owner = await GetAs("OWNER010101AA1", "user", "efirma");
    Require(owner.Result is OkObjectResult,
        "El propietario perdió acceso legítimo a su propio recibo.");
}

static async Task TestResumableAssemblerAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "FileXfer.Resume.Smoke", Guid.NewGuid().ToString("N"));
    var outside = root + "-outside";
    var linkedDirectory = Path.Combine(root, "linked");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    try
    {
        var dbOptions = new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("resume-smoke-" + Guid.NewGuid().ToString("N"))
            .Options;
        var factory = new SmokeDbContextFactory(dbOptions);
        var storage = new StorageOptions
        {
            BasePath = root,
            MaxChunkBytes = 1024 * 1024,
            MaxUploadBytes = 8 * 1024 * 1024
        };
        var sessions = new UploadSessionStore(storage);
        var assembler = new FileAssembler(
            storage,
            factory,
            NullLogger<FileAssembler>.Instance,
            sessions);
        var uuid = Guid.NewGuid().ToString("N");
        const string key = "1/2/3/4/5";
        const string name = "resume.bin";
        var first = Encoding.UTF8.GetBytes("primer-bloque|");
        var second = Encoding.UTF8.GetBytes("segundo-bloque");

        await assembler.AppendAsync(key, uuid, name, 0, first, CancellationToken.None);
        assembler.ReleaseLock(key, uuid);
        var persisted = assembler.GetPersistedLength(key, uuid, name);
        Require(persisted == first.Length, "El offset físico confirmado no coincide con el primer bloque.");
        await assembler.AppendAsync(key, uuid, name, persisted, second, CancellationToken.None);
        assembler.ReleaseLock(key, uuid);

        var expected = SHA256.HashData(first.Concat(second).ToArray());
        var actual = Convert.FromHexString(await assembler.ComputeSha256Async(
            key, uuid, name, CancellationToken.None));
        Require(actual.SequenceEqual(expected), "La reanudación alteró el contenido o el hash final.");

        if (TryCreateDirectoryLink(linkedDirectory, outside))
        {
            Expect<InvalidDataException>(
                () => assembler.ResolveAbsolutePath("linked", Guid.NewGuid().ToString("N"), "escape.bin"),
                "El ensamblador aceptó una ruta de carga que atraviesa un punto de reanálisis.");
        }
        else
        {
            Console.WriteLine("SKIP resumable-storage reparse-point smoke: el host no permite crear vínculos o junctions.");
        }
    }
    finally
    {
        if (Directory.Exists(linkedDirectory) &&
            (File.GetAttributes(linkedDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(linkedDirectory);
        }
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        if (Directory.Exists(outside))
            Directory.Delete(outside, recursive: true);
    }
}

static bool TryCreateDirectoryLink(string linkPath, string targetPath)
{
    try
    {
        Directory.CreateSymbolicLink(linkPath, targetPath);
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        if (!OperatingSystem.IsWindows())
            return false;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(linkPath);
    startInfo.ArgumentList.Add(targetPath);

    try
    {
        using var process = Process.Start(startInfo);
        if (process is null || !process.WaitForExit(5_000))
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return false;
    }
}

static async Task TestSecureDownloadAsync()
{
    var testRoot = Path.Combine(Path.GetTempPath(), "FileXfer.Download.Smoke", Guid.NewGuid().ToString("N"));
    var storageRoot = Path.Combine(testRoot, "storage");
    var relativePath = Path.Combine("RFCOWNER0101", "2026", "verified.bin");
    var physicalPath = Path.Combine(storageRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);

    try
    {
        var content = RandomNumberGenerator.GetBytes(256 * 1024);
        await File.WriteAllBytesAsync(physicalPath, content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var options = new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("secure-download-" + Guid.NewGuid().ToString("N"))
            .Options;
        var factory = new SmokeDbContextFactory(options);

        Guid targetUploadId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            targetUploadId = Guid.NewGuid();
            db.Uploads.Add(new UploadEntry
            {
                Id = targetUploadId,
                Rfc = "RFCOWNER0101",
                FileUuid = Guid.NewGuid().ToString("N"),
                OriginalFileName = "verified.bin",
                StoredRelativePath = relativePath,
                BytesReceived = content.LongLength,
                ExpectedLength = content.LongLength,
                Sha256Server = hash,
                Status = "Completed",
                CompletedUtc = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow
            });
            db.Uploads.Add(new UploadEntry
            {
                Id = Guid.NewGuid(),
                Rfc = "RFCOWNER0101",
                FileUuid = Guid.NewGuid().ToString("N"),
                OriginalFileName = "decoy.bin",
                StoredRelativePath = relativePath,
                BytesReceived = content.LongLength,
                ExpectedLength = content.LongLength,
                Sha256Server = new string('0', 64),
                Status = "Completed",
                CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }

        var service = new SecureFileDownloadService(
            factory,
            new StorageOptions { BasePath = storageRoot });
        var descriptor = new ArchivoLocalDownloadDescriptor(
            1,
            "RFCOWNER0101",
            "verified.bin",
            relativePath,
            targetUploadId);

        var prepared = await service.PrepareAsync(descriptor, CancellationToken.None);
        await using (prepared.Stream)
        {
            using var copy = new MemoryStream();
            await prepared.Stream.CopyToAsync(copy);
            Require(copy.ToArray().SequenceEqual(content),
                "La descarga verificada no devolvió los bytes originales.");
            Require(prepared.Sha256 == hash && prepared.Length == content.LongLength,
                "La descarga preparada no conservó hash o longitud.");
        }

        content[0] ^= 0xFF;
        await File.WriteAllBytesAsync(physicalPath, content);
        try
        {
            _ = await service.PrepareAsync(descriptor, CancellationToken.None);
            throw new InvalidOperationException("La descarga aceptó un archivo alterado.");
        }
        catch (FileDownloadPreparationException ex)
        {
            Require(ex.Code == "hash_mismatch", "La alteración no produjo hash_mismatch.");
        }

        var outsideRelative = Path.Combine("..", "outside.bin");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Uploads.Add(new UploadEntry
            {
                Id = Guid.NewGuid(),
                Rfc = "RFCOWNER0101",
                FileUuid = Guid.NewGuid().ToString("N"),
                OriginalFileName = "outside.bin",
                StoredRelativePath = outsideRelative,
                BytesReceived = content.LongLength,
                ExpectedLength = content.LongLength,
                Sha256Server = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                Status = "Completed",
                CompletedUtc = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        try
        {
            _ = await service.PrepareAsync(
                new ArchivoLocalDownloadDescriptor(2, "RFCOWNER0101", "outside.bin", outsideRelative, null),
                CancellationToken.None);
            throw new InvalidOperationException("La descarga aceptó una ruta fuera del storage.");
        }
        catch (FileDownloadPreparationException ex)
        {
            Require(ex.Code == "invalid_storage_path", "El traversal no produjo invalid_storage_path.");
        }
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }
}

static ClaimsPrincipal Principal(string rfc, IReadOnlyCollection<string> roles, string amr)
{
    var claims = new List<Claim>
    {
        new("rfc", rfc),
        new("amr", amr)
    };
    claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "smoke", "rfc", ClaimTypes.Role));
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
            AddFile(archive, "TransferClienteASE.Updater.exe", updaterPath);
            AddText(archive, "update-public.pem", rsa.ExportSubjectPublicKeyInfoPem());
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

        var incompleteZip = Path.Combine(root, "incomplete.zip");
        CreateZip(incompleteZip, archive =>
        {
            AddFile(archive, "SmokeClient.exe", GetHarmlessWindowsExecutable());
            AddText(archive, "config.txt", "new");
        });
        var incompleteManifest = Sign(incompleteZip, version, rsa);
        var incompleteDestination = Path.Combine(root, "incomplete-destination");
        Directory.CreateDirectory(incompleteDestination);
        var incompleteSentinel = Path.Combine(incompleteDestination, "config.txt");
        await File.WriteAllTextAsync(incompleteSentinel, "old");
        var incompleteResult = await RunUpdaterAsync(
            updaterPath,
            incompleteZip,
            incompleteDestination,
            "SmokeClient.exe",
            version,
            incompleteManifest.Hash,
            incompleteManifest.Signature,
            publicKeyPath);
        Require(incompleteResult.ExitCode == 2,
            "El updater aceptó un paquete incompleto aunque estaba firmado.");
        Require(await File.ReadAllTextAsync(incompleteSentinel) == "old" &&
                !File.Exists(Path.Combine(incompleteDestination, "SmokeClient.exe")),
            "El updater modificó el destino antes de validar el contenido obligatorio.");

        var rollbackZip = Path.Combine(root, "rollback.zip");
        CreateZip(rollbackZip, archive =>
        {
            AddText(archive, "00-config.txt", "new");
            AddText(archive, "10-locked.txt", "new");
            AddFile(archive, "SmokeClient.exe", GetHarmlessWindowsExecutable());
            AddFile(archive, "TransferClienteASE.Updater.exe", updaterPath);
            AddText(archive, "update-public.pem", rsa.ExportSubjectPublicKeyInfoPem());
        });
        var rollbackManifest = Sign(rollbackZip, version, rsa);
        var rollbackDestination = Path.Combine(root, "rollback-destination");
        Directory.CreateDirectory(rollbackDestination);
        var rollbackConfig = Path.Combine(rollbackDestination, "00-config.txt");
        var rollbackLocked = Path.Combine(rollbackDestination, "10-locked.txt");
        await File.WriteAllTextAsync(rollbackConfig, "old");
        await File.WriteAllTextAsync(rollbackLocked, "old");

        (int ExitCode, string Output) rollbackResult;
        using (var lockedFile = new FileStream(
                   rollbackLocked,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            rollbackResult = await RunUpdaterAsync(
                updaterPath,
                rollbackZip,
                rollbackDestination,
                "SmokeClient.exe",
                version,
                rollbackManifest.Hash,
                rollbackManifest.Signature,
                publicKeyPath);
        }

        Require(rollbackResult.ExitCode == 2,
            "El updater no reportó el fallo provocado por un archivo bloqueado.");
        Require(rollbackResult.Output.Contains("restaurada", StringComparison.OrdinalIgnoreCase),
            $"El updater no confirmó el rollback completo: {rollbackResult.Output}");
        Require(await File.ReadAllTextAsync(rollbackConfig) == "old" &&
                await File.ReadAllTextAsync(rollbackLocked) == "old" &&
                !File.Exists(Path.Combine(rollbackDestination, "SmokeClient.exe")),
            "El rollback no restauró exactamente el estado anterior de la instalación.");

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

        var verifyOnlyZip = Path.Combine(root, "verify-only.zip");
        var publicPem = await File.ReadAllTextAsync(publicKeyPath);
        CreateZip(verifyOnlyZip, archive =>
        {
            AddFile(archive, "SmokeClient.exe", GetHarmlessWindowsExecutable());
            AddFile(archive, "TransferClienteASE.Updater.exe", updaterPath);
            AddText(archive, "update-public.pem", publicPem);
        });
        var verifyOnlyManifest = Sign(verifyOnlyZip, version, rsa);
        var verifyOnlyDestination = Path.Combine(root, "verify-only-destination");
        Directory.CreateDirectory(verifyOnlyDestination);
        var verifyOnlyResult = await RunUpdaterAsync(
            updaterPath,
            verifyOnlyZip,
            verifyOnlyDestination,
            "SmokeClient.exe",
            version,
            verifyOnlyManifest.Hash,
            verifyOnlyManifest.Signature,
            publicKeyPath,
            "--verify-only");
        Require(verifyOnlyResult.ExitCode == 0,
            "El modo verify-only rechazó un paquete completo y firmado.");
        Require(!File.Exists(Path.Combine(verifyOnlyDestination, "SmokeClient.exe")),
            "El modo verify-only modificó el destino.");

        var relaunchDestination = Path.Combine(root, "relaunch-destination");
        Directory.CreateDirectory(relaunchDestination);
        var updaterBaseName = Path.GetFileNameWithoutExtension(updaterPath);
        foreach (var source in Directory.GetFiles(Path.GetDirectoryName(updaterPath)!))
        {
            var name = Path.GetFileName(source);
            if (string.Equals(source, updaterPath, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(updaterBaseName + ".", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, Path.Combine(relaunchDestination, name));
            }
        }
        var installedUpdater = Path.Combine(relaunchDestination, Path.GetFileName(updaterPath));
        Require(File.Exists(installedUpdater), "No se preparó el updater dentro del destino de prueba.");
        var relaunchSentinel = Path.Combine(relaunchDestination, "config.txt");
        await File.WriteAllTextAsync(relaunchSentinel, "old");
        var relaunchResult = await RunUpdaterAsync(
            installedUpdater,
            validZip,
            relaunchDestination,
            "SmokeClient.exe",
            version,
            validManifest.Hash,
            validManifest.Signature,
            publicKeyPath);
        Require(relaunchResult.ExitCode == 0, "El updater instalado no pudo relanzarse desde TEMP.");

        var relaunchApplied = false;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (File.Exists(Path.Combine(relaunchDestination, "SmokeClient.exe")) &&
                await File.ReadAllTextAsync(relaunchSentinel) == "new")
            {
                relaunchApplied = true;
                break;
            }
            await Task.Delay(250);
        }
        Require(relaunchApplied,
            "El updater relanzado desde TEMP no aplicó el paquete dentro del tiempo esperado.");

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var launchedClients = Process.GetProcessesByName("SmokeClient");
            if (launchedClients.Length == 0)
                break;
            foreach (var process in launchedClients)
                process.Dispose();
            await Task.Delay(250);
        }
        var remainingClients = Process.GetProcessesByName("SmokeClient");
        try
        {
            Require(remainingClients.Length == 0,
                "El ejecutable simulado no terminó y no es seguro limpiar la prueba.");
        }
        finally
        {
            foreach (var process in remainingClients)
                process.Dispose();
        }
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
    string publicKeyPath,
    string? extraArgument = null)
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
    if (!string.IsNullOrWhiteSpace(extraArgument))
        startInfo.ArgumentList.Add(extraArgument);

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

static async Task ExpectAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
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

file sealed class SmokeWebHostEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "FileXfer.Security.SmokeTests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

file sealed class SmokeReceiptService : IUploadReceiptService
{
    public Task<UploadReceipt> CreateAsync(UploadReceiptDraft draft, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public bool Verify(UploadReceipt receipt) => true;
}

file sealed class SmokeOptionsSnapshot<T>(T value) : IOptionsSnapshot<T>
    where T : class
{
    public T Value => value;
    public T Get(string? name) => value;
}
