using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Uploading;
using FileXfer.Server.Controllers.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Reflection;

internal static class PendingMaintenanceSmokeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "FileXfer-PendingSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new StorageOptions { BasePath = root, ResumeRetentionHours = 24 };
            var factory = new TestFactory();
            var sessions = new UploadSessionStore(storage);
            using var logs = LoggerFactory.Create(_ => { });
            var assembler = new FileAssembler(storage, factory, logs.CreateLogger<FileAssembler>(), sessions);
            var service = new PendingTransferMaintenanceService(
                factory, assembler, sessions, storage, logs.CreateLogger<PendingTransferMaintenanceService>());
            var now = DateTimeOffset.UtcNow;
            using (var sqlContext = new FileXferDbContext(new DbContextOptionsBuilder<FileXferDbContext>()
                .UseSqlServer("Server=smoke.invalid;Database=SqlTranslationOnly;Integrated Security=True",
                    sql => sql.UseCompatibilityLevel(120)).Options))
            {
                var query = PendingTransferMaintenanceService.ExpiredQuery(sqlContext, now, 24)
                    .Skip(1000).Select(upload => upload.Id).Take(1000).ToQueryString();
                Require(!query.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
                    "La consulta de vencidos no es compatible con SQL Server 2014.");
                var referenceQuery = (IQueryable)typeof(PendingTransferMaintenanceService)
                    .GetMethod("FindReferences", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { sqlContext, "TST010101AAA/FILE.BIN", Guid.NewGuid() })!;
                var referenceSql = referenceQuery.ToQueryString();
                Require(!referenceSql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase) &&
                    referenceSql.Contains("REPLACE", StringComparison.OrdinalIgnoreCase),
                    "La consulta de referencias compartidas no traduce de forma compatible con SQL Server 2014.");
                var paths = new[] { "TST010101AAA/FILE.BIN", "TST010101AAA/OTHER.BIN" };
                var assessmentSql = sqlContext.Uploads.AsNoTracking().Where(other =>
                    EF.Constant(paths).Contains(other.StoredRelativePath.Replace("\\", "/").ToUpper())).ToQueryString();
                Require(!assessmentSql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase),
                    "El diagnóstico de referencias usa OPENJSON incompatible con SQL Server 2014.");
            }
            Require(typeof(PendingTransfersController).GetMethod(nameof(PendingTransfersController.Reconcile))!
                .GetCustomAttributes<AuthorizeAttribute>().Any(attribute => attribute.Policy == "AdminEfirma"),
                "La conciliación de metadatos no exige e.firma administrativa.");
            var manual = await SeedAsync(factory, assembler, "Pending", now.AddHours(1), now, "manual.bin");
            var active = await SeedAsync(factory, assembler, "Pending", now.AddHours(-1), now, "active.bin");
            var expired = await SeedAsync(factory, assembler, "Paused", now.AddSeconds(-1), now, "expired.bin");
            var legacy = await SeedAsync(factory, assembler, "Pending", null, now.AddDays(-2), "legacy.bin");
            var future = await SeedAsync(factory, assembler, "Paused", now.AddHours(1), now, "future.bin");
            var completed = await SeedAsync(factory, assembler, "Completed", null, now, "completed.bin");
            Require(sessions.TryBegin(active.FileUuid, active.Rfc) == UploadStartResult.Started, "No se preparó la sesión activa.");

            var page = await service.GetPendingAsync(1, 25, CancellationToken.None);
            Require(page.TotalCount == 5 && page.Items.Single(x => x.UploadId == active.Id).IsActive,
                "La lista no refleja pendientes y sesión activa.");
            Require(page.Items.Single(x => x.UploadId == legacy.Id).IsExpired,
                "No se calculó la expiración del registro legado sin fecha explícita.");
            var cleanup = await service.CleanupExpiredAsync("system", null, CancellationToken.None);
            Require(cleanup.Removed == 2 && cleanup.Skipped == 1 && cleanup.Failed == 0,
                "La limpieza no eliminó ambos vencidos o no respetó el activo.");
            Require(cleanup.BytesReclaimed == 8, "La limpieza no informó los bytes físicos liberados.");
            Require(cleanup.Items.Count == 3 && cleanup.Items.Any(item => item.UploadId == active.Id &&
                item.Outcome == "Skipped" && item.Code == "ActiveTransfer" && item.Message.Contains("activa")),
                "La limpieza no explicó por archivo por qué no pudo retirar la transferencia activa.");
            Require(!Exists(assembler, expired) && !Exists(assembler, legacy) &&
                Exists(assembler, active) && Exists(assembler, future) && Exists(assembler, completed),
                "La limpieza modificó un archivo activo, vigente o completado.");
            sessions.End(active.FileUuid);

            var manualResult = await service.RemoveAsync(
                new[] { manual.Id, completed.Id }, "Prueba de retiro administrativo", "TST010101AAA", "127.0.0.1", CancellationToken.None);
            Require(manualResult.Removed == 1 && manualResult.Skipped == 1 && manualResult.Failed == 0,
                "La eliminación manual no protegió el documento completado.");
            Require(!Exists(assembler, manual) && Exists(assembler, completed),
                "La eliminación manual no afectó únicamente a la copia pendiente.");
            await using (var db = factory.CreateDbContext())
            {
                Require((await db.Uploads.SingleAsync(x => x.Id == manual.Id)).Status == "Cancelled",
                    "El retiro manual no conservó estado terminal y auditoría.");
                Require((await db.Uploads.SingleAsync(x => x.Id == expired.Id)).Status == "Expired",
                    "El vencimiento no conservó estado terminal.");
                Require(await db.AuditLogs.CountAsync() == 3 &&
                    await db.AuditLogs.AnyAsync(x => x.ActorRfc == "TST010101AAA" && x.Details!.Contains("Prueba de retiro")),
                    "La limpieza no conservó actor, motivo e historial auditable.");
            }

            var lockUuid = Guid.NewGuid().ToString("N");
            Require(sessions.TryAcquireMaintenance(lockUuid, out var lease), "No se obtuvo exclusión de mantenimiento.");
            Require(sessions.TryBegin(lockUuid, "TST010101AAA") == UploadStartResult.DuplicateUuid,
                "Una carga inició mientras se eliminaba su parcial.");
            lease!.Dispose();
            Require(sessions.TryBegin(lockUuid, "TST010101AAA") == UploadStartResult.Started,
                "El mantenimiento dejó bloqueado permanentemente el UUID.");
            Require(!sessions.TryAcquireMaintenance(lockUuid, out _), "El mantenimiento tomó una carga activa.");
            sessions.End(lockUuid);
            var repeat = await service.RemoveAsync(new[] { manual.Id }, "Prueba de repetición idempotente",
                "TST010101AAA", null, CancellationToken.None);
            Require(repeat.Removed == 0 && repeat.Skipped == 1,
                "La repetición alteró un registro terminal.");

            var shared = await SeedAsync(factory, assembler, "Pending", now.AddHours(1), now, "shared.bin");
            var inconsistent = await SeedAsync(factory, assembler, "Pending", now.AddHours(1), now, "inconsistent.bin");
            var receipted = await SeedAsync(factory, assembler, "Pending", now.AddHours(1), now, "receipt.bin");
            await using (var db = factory.CreateDbContext())
            {
                db.Uploads.Add(new UploadEntry
                {
                    Id = Guid.NewGuid(), FileUuid = Guid.NewGuid().ToString("N"), Rfc = shared.Rfc,
                    Status = "Completed", StoredRelativePath = shared.StoredRelativePath
                });
                var inconsistentEntry = await db.Uploads.SingleAsync(x => x.Id == inconsistent.Id);
                inconsistentEntry.CompletedUtc = now;
                db.UploadReceipts.Add(new UploadReceipt
                {
                    Id = Guid.NewGuid(), UploadId = receipted.Id, IssuedUtc = now,
                    CanonicalPayloadJson = "{}", PayloadSha256 = "hash", ChainHash = "chain",
                    SignatureBase64 = "signature", KeyId = "smoke", PublicKeyPem = "public-key"
                });
                await db.SaveChangesAsync();
            }
            var protectedResult = await service.RemoveAsync(
                new[] { shared.Id, inconsistent.Id, receipted.Id }, "Prueba de protección de evidencia",
                "TST010101AAA", null, CancellationToken.None);
            Require(protectedResult.Removed == 0 && protectedResult.RetiredPreservingFile == 1 &&
                protectedResult.Skipped == 2 && protectedResult.Failed == 0 &&
                Exists(assembler, shared) && Exists(assembler, inconsistent) && Exists(assembler, receipted),
                "La limpieza no retiró el registro compartido preservando su evidencia, o alteró evidencia protegida.");
            Require(protectedResult.Items.Single(item => item.UploadId == shared.Id).Code == "SharedPhysicalFilePreserved" &&
                protectedResult.Items.Single(item => item.UploadId == receipted.Id).Code == "ProtectedEvidence" &&
                protectedResult.BytesReclaimed == 0,
                "El resultado no distingue retiro de metadatos, protección por recibo y espacio realmente liberado.");

            // A shared physical file is preserved, including when the expired row is
            // retired automatically; a live alias blocks the retirement until it stops.
            var expiredShared = await SeedAsync(factory, assembler, "Paused", now.AddHours(-1), now, "expired-shared.bin");
            var aliasId = Guid.NewGuid();
            var aliasUuid = Guid.NewGuid().ToString("N");
            await using (var db = factory.CreateDbContext())
            {
                db.Uploads.Add(new UploadEntry
                {
                    Id = aliasId, FileUuid = aliasUuid, Rfc = expiredShared.Rfc,
                    Status = "Completed", CompletedUtc = now, StoredRelativePath = expiredShared.StoredRelativePath
                });
                await db.SaveChangesAsync();
            }
            sessions.TryBegin(aliasUuid, expiredShared.Rfc);
            var aliasProtected = await service.RemoveAsync(new[] { expiredShared.Id }, "Prueba alias activo",
                "TST010101AAA", null, CancellationToken.None);
            Require(aliasProtected.Skipped == 1 && aliasProtected.Items.Single().Code == "ActiveSharedReference" &&
                Exists(assembler, expiredShared), "El retiro ignoró que otra referencia al archivo está activa.");
            sessions.End(aliasUuid);
            var sharedCleanup = await service.CleanupExpiredAsync("system", null, CancellationToken.None);
            var sharedItem = sharedCleanup.Items.Single(item => item.UploadId == expiredShared.Id);
            Require(sharedItem.Outcome == "RetiredPreservingFile" && sharedItem.BytesReclaimed == 0 &&
                Exists(assembler, expiredShared), "La limpieza automática borró un archivo físico compartido.");
            await using (var db = factory.CreateDbContext())
            {
                Require((await db.Uploads.SingleAsync(item => item.Id == expiredShared.Id)).Status == "Expired" &&
                    (await db.Uploads.SingleAsync(item => item.Id == aliasId)).Status == "Completed" &&
                    await db.AuditLogs.AnyAsync(item => item.Action == "PendingTransferExpired" &&
                        item.Details!.Contains(expiredShared.Id.ToString()) && item.Details.Contains("PhysicalAction=Preserved")),
                    "La expiración compartida no conservó estado/evidencia/auditoría consistentes.");
            }

            // The stored path belongs to a legacy context. Listing, diagnosis and
            // reconciliation must not create the new context's directory or touch it.
            var mismatch = await SeedAsync(factory, assembler, "Paused", now.AddHours(-1), now, "legacy-context.bin");
            var originalFile = assembler.ResolveAbsolutePath(mismatch.Rfc, mismatch.FileUuid, "legacy-context.bin");
            var originalBytes = await File.ReadAllBytesAsync(originalFile);
            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Uploads.SingleAsync(item => item.Id == mismatch.Id);
                row.IdEnte = 912; row.IdAuditoria = 913; row.IdActo = 914; row.IdOficio = 915; row.IdRequerimiento = 916;
                await db.SaveChangesAsync();
            }
            var mismatchPage = await service.GetPendingAsync(1, 100, CancellationToken.None);
            var mismatchDto = mismatchPage.Items.Single(item => item.UploadId == mismatch.Id);
            Require(mismatchDto.CanReconcile && mismatchDto.ProtectionCode == "ContextPathMismatch" &&
                mismatchDto.ProtectionReason!.Contains("contexto"), "La lista no ofrece diagnóstico y conciliación de la ruta inconsistente.");
            var diagnostics = await service.GetDiagnosticsAsync(CancellationToken.None);
            Require(diagnostics.ContextMismatches >= 1 && !diagnostics.IsSampled,
                "El diagnóstico de salud no informa la causa real de los pendientes vencidos.");
            var mismatchCleanup = await service.CleanupExpiredAsync("system", null, CancellationToken.None);
            Require(mismatchCleanup.Items.Single(item => item.UploadId == mismatch.Id).Code == "ContextPathMismatch" &&
                !Directory.Exists(Path.Combine(root, "912")), "La limpieza abrió o creó una ruta de contexto inconsistente.");
            var reconciled = await service.ReconcileAsync(new[] { mismatch.Id }, "Registro legado revisado; conservar archivo",
                "TST010101AAA", "127.0.0.1", CancellationToken.None);
            Require(reconciled.RetiredPreservingFile == 1 && reconciled.BytesReclaimed == 0 &&
                reconciled.Items.Single().Code == "MetadataReconciled" &&
                (await File.ReadAllBytesAsync(originalFile)).SequenceEqual(originalBytes) &&
                !Directory.Exists(Path.Combine(root, "912")),
                "La conciliación de metadatos modificó el archivo o creó una ubicación incorrecta.");
            await using (var db = factory.CreateDbContext())
            {
                Require((await db.Uploads.SingleAsync(item => item.Id == mismatch.Id)).Status == "Expired" &&
                    await db.AuditLogs.AnyAsync(item => item.Action == "PendingTransferReconciled" &&
                        item.Details!.Contains(mismatch.Id.ToString()) && item.Details.Contains("PhysicalAction=Preserved")),
                    "La conciliación manual no registró cierre y conservación física explícita.");
            }

            var completedReconcile = await service.ReconcileAsync(new[] { completed.Id, receipted.Id }, "Prueba evidencia protegida",
                "TST010101AAA", null, CancellationToken.None);
            Require(completedReconcile.Skipped == 2 && completedReconcile.RetiredPreservingFile == 0,
                "La conciliación manual ignoró un cierre o recibo protegido.");

            // A reserved legacy filename would be sanitized to a different file.
            // It must never delete the valid canonical name located beside it.
            var reservedId = Guid.NewGuid();
            var canonicalSentinel = Path.Combine(root, "TST010101AAA", "_CON.bin");
            await File.WriteAllBytesAsync(canonicalSentinel, new byte[] { 8, 7, 6 });
            await using (var db = factory.CreateDbContext())
            {
                db.Uploads.Add(new UploadEntry
                {
                    Id = reservedId, FileUuid = Guid.NewGuid().ToString("N"), Rfc = "TST010101AAA",
                    Status = "Pending", StoredRelativePath = "TST010101AAA/CON.bin",
                    CreatedUtc = now.AddDays(-2), LastActivityUtc = now.AddDays(-2), ResumeExpiresUtc = now.AddHours(-1)
                });
                await db.SaveChangesAsync();
            }
            var reservedResult = await service.RemoveAsync(new[] { reservedId }, "Prueba nombre reservado",
                "TST010101AAA", null, CancellationToken.None);
            Require(reservedResult.Skipped == 1 && reservedResult.Items.Single().Code == "ContextPathMismatch" &&
                (await File.ReadAllBytesAsync(canonicalSentinel)).SequenceEqual(new byte[] { 8, 7, 6 }),
                "Un nombre legado reservado eliminó el archivo con nombre canónico distinto.");

            var backlogFactory = new TestFactory();
            var backlogSessions = new UploadSessionStore(storage);
            var backlogAssembler = new FileAssembler(storage, backlogFactory, logs.CreateLogger<FileAssembler>(), backlogSessions);
            var backlogService = new PendingTransferMaintenanceService(
                backlogFactory, backlogAssembler, backlogSessions, storage,
                logs.CreateLogger<PendingTransferMaintenanceService>());
            await using (var db = backlogFactory.CreateDbContext())
            {
                db.Uploads.AddRange(Enumerable.Range(0, 1000).Select(index => new UploadEntry
                {
                    Id = Guid.NewGuid(), FileUuid = Guid.NewGuid().ToString("N"), Rfc = "TST010101AAA",
                    Status = "Pending", CreatedUtc = now.AddDays(-3), LastActivityUtc = now.AddDays(-3),
                    ResumeExpiresUtc = now.AddDays(-2), ReceiptId = Guid.NewGuid(),
                    StoredRelativePath = $"protected-{index}.bin"
                }));
                await db.SaveChangesAsync();
            }
            var beyondProtected = await SeedAsync(backlogFactory, backlogAssembler, "Paused",
                now.AddHours(-1), now.AddDays(-1), "beyond-protected.bin");
            var firstPass = await backlogService.CleanupExpiredAsync("system", null, CancellationToken.None);
            Require(firstPass.Removed == 0 && firstPass.Skipped == 1000,
                "La primera pasada no respetó el límite de 1000 candidatos protegidos.");
            var secondPass = await backlogService.CleanupExpiredAsync("system", null, CancellationToken.None);
            Require(secondPass.Removed == 1 && !Exists(backlogAssembler, beyondProtected),
                "Los pendientes protegidos bloquearon permanentemente la limpieza de archivos posteriores.");

            Console.WriteLine("PASS: pending cleanup expires legacy/current partials, skips active/completed, preserves audit and serializes resume/removal.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool Exists(FileAssembler assembler, UploadEntry entry) =>
        File.Exists(assembler.ResolveAbsolutePath(entry.Rfc, entry.FileUuid, Path.GetFileName(entry.StoredRelativePath)));

    private static async Task<UploadEntry> SeedAsync(
        TestFactory factory, FileAssembler assembler, string status, DateTimeOffset? expiry,
        DateTimeOffset activity, string fileName)
    {
        var upload = new UploadEntry
        {
            Id = Guid.NewGuid(), FileUuid = Guid.NewGuid().ToString("N"), Rfc = "TST010101AAA",
            OriginalFileName = fileName, Status = status, ResumeExpiresUtc = expiry,
            CreatedUtc = activity, LastActivityUtc = activity, BytesReceived = 4, ExpectedLength = 8,
            ReceiptId = status == "Completed" ? Guid.NewGuid() : null
        };
        var path = assembler.ResolveAbsolutePath(upload.Rfc, upload.FileUuid, fileName);
        upload.StoredRelativePath = assembler.GetStoredRelativePath(path);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
        await using var db = factory.CreateDbContext();
        db.Uploads.Add(upload);
        await db.SaveChangesAsync();
        return upload;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestFactory : IDbContextFactory<FileXferDbContext>
    {
        private readonly DbContextOptions<FileXferDbContext> _options =
            new DbContextOptionsBuilder<FileXferDbContext>()
                .UseInMemoryDatabase("pending-smoke-" + Guid.NewGuid().ToString("N")).Options;
        public FileXferDbContext CreateDbContext() => new(_options);
        public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
