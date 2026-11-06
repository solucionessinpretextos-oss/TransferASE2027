using System.Reflection;
using System.Security.Claims;
using FileXfer.Server.Controllers.Admin;
using FileXfer.Server.Data;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Options;
using FileXfer.Server.Middleware;
using FileXfer.Server.Services;
using FileXfer.Server.Uploading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

await TestStatisticsAndSummaryAsync();
await TestAlertStatisticsHealthAsync();
await TestLegacyActivityFallbackAsync();
TestSqlServer2014QueryTranslation();
TestHealthExplanations();
await TestAdministrativeContractAsync();
await TestDashboardCanonicalPathAsync();
await DashboardHttpTests.RunAsync();
await AlertOperationsHttpTests.RunAsync();
await AlertReportsSmokeTests.RunAsync();
Console.WriteLine("PASS monitoring reset=durable-and-audited history-and-receipts=preserved statistics-window=bounded live-health=not-hidden active-transfers=not-double-counted pending-expiry=distinct-from-idle legacy-activity=creation-fallback sql2014=queries-translated-without-openjson health-formula=explained dashboard-path=canonical-get-head-308");
return 0;

static async Task TestAlertStatisticsHealthAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("alert-health-" + Guid.NewGuid().ToString("N")).Options;
    var factory = new TestDbFactory(options);
    var stats = new AlertStatisticsService(factory);
    var now = DateTimeOffset.UtcNow;
    var old = Alert(1, AlertOutboxStatuses.DeadLetter, now.AddMinutes(-1));
    var pending = Alert(2, AlertOutboxStatuses.Pending, now.AddMinutes(-2));
    await using (var db = factory.CreateDbContext())
    {
        db.AlertOutboxDeliveries.AddRange(old, pending);
        await db.SaveChangesAsync();
    }

    using var queue = new AlertDeliveryQueue(factory, new LogAlertSink(NullLogger<LogAlertSink>.Instance), [],
        Options.Create(new AlertOptions { Enabled = true, WorkerCount = 1, QueueCapacity = 3, PollIntervalMilliseconds = 100 }),
        NullLogger<AlertDeliveryQueue>.Instance);
    await queue.StartAsync(default);
    try
    {
        await ExpectSnapshotAsync(s => s.DeadLetter == 1 && s.Pending == 1 && Score(s) == 75);
        var reset = await stats.ResetAsync("CARJ721209SD2", null, "Se corrigió el canal de correo", default);
        await ExpectSnapshotAsync(s => s.DeadLetter == 0 && s.Pending == 1 && Score(s) == 100);

        await using (var db = factory.CreateDbContext())
        {
            Require(await db.AlertOutboxDeliveries.AnyAsync(row => row.Id == old.Id && row.AttemptCount == 3),
                "El período nuevo modificó el aviso agotado del historial.");
            db.AlertOutboxDeliveries.Add(Alert(3, AlertOutboxStatuses.DeadLetter, reset.TimestampUtc));
            await db.SaveChangesAsync();
        }
        await ExpectSnapshotAsync(s => s.DeadLetter == 1 && Score(s) == 75);

        await stats.ResetAsync("CARJ721209SD2", null, "Corte posterior con cola activa", default);
        await using (var db = factory.CreateDbContext())
        {
            db.AlertOutboxDeliveries.Add(Alert(4, AlertOutboxStatuses.Pending, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await ExpectSnapshotAsync(s => s.DeadLetter == 0 && s.Pending == 2 && Score(s) == 75);
        Console.WriteLine("PASS alert-health previous-exhaustion=excluded new-exhaustion=penalized saturation=preserved");
    }
    finally { await queue.StopAsync(default); }

    async Task ExpectSnapshotAsync(Func<AlertDeliverySnapshot, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (predicate(queue.GetSnapshot())) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("La salud de alertas no reflejó el período y la cola esperados.");
    }

    static double Score(AlertDeliverySnapshot snapshot) => OperationalHealthCalculator.Score(
        OperationalHealthCalculator.Explain(new ServerHealthDto
        {
            IsHealthy = snapshot.IsHealthy, DatabaseOk = true, StorageOk = true, MessageBusOk = true,
            AlertingOk = snapshot.IsHealthy, AlertDeadLetter = snapshot.DeadLetter, AlertQueuePending = snapshot.Pending
        }, 0, 0, 0));

    static AlertOutboxDelivery Alert(int id, string status, DateTimeOffset updatedUtc) => new()
    {
        EventId = Guid.NewGuid(), Transport = "email", DeduplicationKey = $"fixture-{id}", Title = "Alerta de prueba",
        Message = "Evento sintético", Status = status, OccurredUtc = updatedUtc, CreatedUtc = updatedUtc,
        UpdatedUtc = updatedUtc, NextAttemptUtc = updatedUtc.AddHours(1),
        AttemptCount = status == AlertOutboxStatuses.DeadLetter ? 3 : 0,
        LastFailureCode = status == AlertOutboxStatuses.DeadLetter ? "fixture-error" : null
    };
}

static async Task TestStatisticsAndSummaryAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("monitoring-smoke-" + Guid.NewGuid().ToString("N")).Options;
    var factory = new TestDbFactory(options);
    var storage = new StorageOptions { ResumeRetentionHours = 24 };
    var sessions = new UploadSessionStore(storage);
    var now = DateTimeOffset.UtcNow;
    var oldSuccess = Upload("Completed", now.AddMinutes(-10), 100);
    oldSuccess.CompletedUtc = now.AddMinutes(-5);
    oldSuccess.ReceiptId = Guid.NewGuid();
    var oldFailure = Upload("Failed", now.AddMinutes(-10), 0);
    oldFailure.CompletedUtc = now.AddMinutes(-4);
    var cancelled = Upload("Cancelled", now.AddMinutes(-10), 0);
    cancelled.LastError = "Eliminación manual por operador de pruebas";
    cancelled.CompletedUtc = now.AddMinutes(-3);
    var expiredHistory = Upload("Expired", now.AddMinutes(-10), 0);
    expiredHistory.CompletedUtc = now.AddMinutes(-2);

    var sending = Upload("Pending", now.AddHours(-26), 10);
    sending.ResumeExpiresUtc = now.AddHours(-1);
    var waiting = Upload("Pending", now.AddHours(-26), 20);
    waiting.ResumeExpiresUtc = now.AddHours(-1);
    Require(sessions.TryBegin(sending.FileUuid, sending.Rfc) == UploadStartResult.Started, "No se abrió sesión de envío.");
    sessions.Update(sending.FileUuid, 10);
    Require(sessions.TryBegin(waiting.FileUuid, waiting.Rfc) == UploadStartResult.Started, "No se abrió sesión en espera.");

    var idle = Upload("Paused", now.AddHours(-1), 30);
    idle.ResumeExpiresUtc = now.AddHours(5);
    var expired = Upload("Paused", now.AddHours(-2), 40);
    expired.ResumeExpiresUtc = now.AddMinutes(-1);
    var legacyExpired = Upload("Pending", now.AddHours(-25), 50);
    legacyExpired.ResumeExpiresUtc = null;
    var fresh = Upload("Pending", now.AddMinutes(-1), 60);
    fresh.ResumeExpiresUtc = now.AddHours(1);
    var previousAudit = Guid.NewGuid();
    await using (var db = factory.CreateDbContext())
    {
        db.Uploads.AddRange(oldSuccess, oldFailure, cancelled, expiredHistory, sending, waiting, idle, expired, legacyExpired, fresh);
        db.UploadReceipts.Add(new UploadReceipt
        {
            Id = oldSuccess.ReceiptId.Value, UploadId = oldSuccess.Id, IssuedUtc = oldSuccess.CompletedUtc.Value,
            CanonicalPayloadJson = "{}", PayloadSha256 = "test-hash", ChainHash = "test-chain",
            SignatureBase64 = "dGVzdA==", KeyId = "test-key", PublicKeyPem = "test-public-key"
        });
        db.AuditLogs.Add(new AuditLog { Id = previousAudit, TimestampUtc = now.AddHours(-1), Action = "Test.ExistingAudit" });
        await db.SaveChangesAsync();
    }

    var controller = new MonitoringController(factory, new HealthyServer(), sessions, storage);
    var before = await SummaryAsync(controller);
    Require(before.Transfers.ActiveTransfers == 1 && before.Transfers.WaitingTransfers == 1 && before.Transfers.TotalPendingTransfers == 2,
        "Las sesiones en espera se contaron de nuevo como transmisiones activas.");
    Require(before.Kpis.PendingTransfers == 6 && before.Kpis.PendingBytes == 210, "El backlog durable perdió pendientes antiguos o bytes.");
    Require(before.Kpis.StalePendingTransfers == 1, "Se confundió inactividad con vencimiento, o se penalizó una sesión activa.");
    Require(before.Kpis.ExpiredPendingTransfers == 2, "El vencimiento individual o el plazo legado no se reconoció.");
    Require(before.Kpis.ExpiringNext24Hours == 2 && before.Kpis.NearExpiryBytes == 90,
        "Las próximas expiraciones no excluyeron activos/vencidos.");
    Require(before.Kpis.OldestPendingAgeHours >= 25, "No se informó la antigüedad del pendiente más viejo.");
    Require(before.Transfers.FailedLastHour == 1, "Cancelaciones administrativas o expiraciones inflaron la tasa de fallas técnicas.");
    Require(before.Kpis.OperationalHealthScore == 59, $"Se esperaba salud 59, se obtuvo {before.Kpis.OperationalHealthScore}.");

    var statistics = new MonitoringStatisticsService(factory);
    var resetUtc = await statistics.ResetAsync("CARJ721209SD2", "127.0.0.1", "Inicio de prueba de medición", default);
    await using (var reopenedDb = factory.CreateDbContext())
    {
        Require(await MonitoringStatisticsService.GetStartedUtcAsync(reopenedDb, default) == resetUtc,
            "El reinicio se perdió al recrear el servicio/contexto.");
        Require(await reopenedDb.Uploads.CountAsync() == 10 && await reopenedDb.UploadReceipts.CountAsync() == 1,
            "Reiniciar estadísticas eliminó transferencias o recibos.");
        Require(await reopenedDb.AuditLogs.AnyAsync(x => x.Id == previousAudit), "El reinicio borró auditoría previa.");
        var resetAudit = await reopenedDb.AuditLogs.SingleAsync(x => x.Action == MonitoringStatisticsService.ResetAction);
        Require(resetAudit.ActorRfc == "CARJ721209SD2" && resetAudit.Details!.Contains("Inicio de prueba"), "El reinicio no registró responsable y motivo.");
    }

    // A fresh controller/service instance must observe the durable marker immediately.
    var after = await SummaryAsync(new MonitoringController(factory, new HealthyServer(), sessions, storage));
    Require(after.StatisticsStartedUtc == resetUtc, "El resumen no informa el inicio de la medición.");
    Require(after.Transfers.CompletedToday == 0 && after.Transfers.FailedToday == 0 && after.Transfers.CompletedLastHour == 0 && after.Transfers.FailedLastHour == 0,
        "El reinicio no separó actividad anterior de la nueva ventana.");
    Require(after.Kpis.PendingTransfers == before.Kpis.PendingTransfers && after.Kpis.OperationalHealthScore == before.Kpis.OperationalHealthScore,
        "El reinicio ocultó pendientes o mejoró artificialmente la salud.");
    Require(after.RecentTransfers.Count == 6 && after.RecentTransfers.All(x => x.Status is "Pending" or "Paused"),
        "La ventana posterior al reinicio perdió el backlog o conserva cierres previos.");

    var newSuccess = Upload("Completed", DateTimeOffset.UtcNow, 200);
    newSuccess.CompletedUtc = DateTimeOffset.UtcNow;
    await using (var db = factory.CreateDbContext())
    {
        db.Uploads.Add(newSuccess);
        await db.SaveChangesAsync();
    }
    var newPeriod = await SummaryAsync(controller);
    Require(newPeriod.Transfers.CompletedToday == 1 && newPeriod.Transfers.TotalBytesTransferredToday == 200 &&
            newPeriod.Transfers.FailedToday == 0 && newPeriod.Kpis.TotalTransfersToday == 1,
        "La nueva actividad no se cuenta en el período reiniciado.");
    Require(newPeriod.Kpis.SuccessRateTodayPercent == 100 && newPeriod.StatisticsTimeZone == "America/Mazatlan",
        "La tasa o zona de la ventana nueva no coincide.");

    foreach (var invalid in new[] { "", "    ", "hola", new string('x', 501) })
        await ExpectArgumentAsync(() => statistics.ResetAsync("CARJ721209SD2", null, invalid, default));
    await using var finalDb = factory.CreateDbContext();
    Require(await finalDb.AuditLogs.CountAsync(x => x.Action == MonitoringStatisticsService.ResetAction) == 1,
        "Una solicitud inválida creó una nueva ventana de estadísticas.");
}

static async Task TestLegacyActivityFallbackAsync()
{
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseInMemoryDatabase("monitoring-legacy-activity-" + Guid.NewGuid().ToString("N")).Options;
    var factory = new TestDbFactory(options);
    var storage = new StorageOptions { ResumeRetentionHours = 24 };
    var now = DateTimeOffset.UtcNow;
    var recent = Upload("Pending", now.AddMinutes(-10), 10);
    recent.LastActivityUtc = default;
    var expired = Upload("Paused", now.AddHours(-25), 20);
    expired.LastActivityUtc = default;
    await using (var db = factory.CreateDbContext())
    {
        db.Uploads.AddRange(recent, expired);
        await db.SaveChangesAsync();
    }

    var summary = await SummaryAsync(new MonitoringController(factory, new HealthyServer(), new UploadSessionStore(storage), storage));
    Require(summary.Kpis.PendingTransfers == 2 && summary.Kpis.ExpiredPendingTransfers == 1,
        "Una actividad legada sin fecha expiró incorrectamente o ignoró CreatedUtc como respaldo.");
    Require(summary.Kpis.ExpiringNext24Hours == 1 && summary.Kpis.NearExpiryBytes == 10,
        "El pendiente legado reciente no conservó su plazo restante desde CreatedUtc.");
    Require(summary.Kpis.StalePendingTransfers == 0 && summary.Kpis.OldestPendingAgeHours is >= 25 and < 26,
        "La actividad por defecto produjo edad desde año 1 o una falsa alerta de inactividad.");
}

static void TestSqlServer2014QueryTranslation()
{
    // ToQueryString compiles the production-provider SQL without opening a connection.
    // SQL Server 2014 (compatibility level 120) does not support EF8's OPENJSON path.
    var options = new DbContextOptionsBuilder<FileXferDbContext>()
        .UseSqlServer("Server=sql2014.invalid;Database=translation_only;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
            sql => sql.UseCompatibilityLevel(120)).Options;
    using var db = new FileXferDbContext(options);
    var now = DateTimeOffset.UtcNow;
    var fallbackCutoff = now.AddHours(-24);
    var staleCutoff = now.AddMinutes(-30);
    var nearExpiry = now.AddHours(24);
    var resetUtc = (DateTimeOffset?)now.AddMinutes(-5);

    foreach (var activeUuids in new[] { Array.Empty<string>(), new[] { "e5ab829d7d414b66bf74809c5adfbdfa" } })
    {
        var inactive = db.Uploads.AsNoTracking().Where(u =>
            (u.Status == "Pending" || u.Status == "Paused") && u.CompletedUtc == null &&
            !EF.Constant(activeUuids).Contains(u.FileUuid));
        var expired = inactive.Where(u =>
            (u.ResumeExpiresUtc != null && u.ResumeExpiresUtc <= now) ||
            (u.ResumeExpiresUtc == null &&
                (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) <= fallbackCutoff));
        var notExpiredIdle = inactive.Where(u =>
            (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) < staleCutoff &&
            ((u.ResumeExpiresUtc != null && u.ResumeExpiresUtc > now) ||
             (u.ResumeExpiresUtc == null &&
                (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) > fallbackCutoff)));
        var aboutToExpire = inactive.Where(u =>
            (u.ResumeExpiresUtc > now && u.ResumeExpiresUtc <= nearExpiry) ||
            (u.ResumeExpiresUtc == null &&
                (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) > fallbackCutoff &&
                (u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc) <= now));
        var ageOrder = inactive.OrderBy(u => u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc)
            .Select(u => (DateTimeOffset?)(u.LastActivityUtc == default ? u.CreatedUtc : u.LastActivityUtc)).Take(1);
        var bytes = expired.GroupBy(u => 1).Select(group => group.Sum(u => u.BytesReceived));
        var recent = db.Uploads.Where(u => resetUtc == null || u.CreatedUtc >= resetUtc ||
                u.CompletedUtc >= resetUtc || u.Status == "Pending" || u.Status == "Paused")
            .OrderByDescending(u => u.CreatedUtc).Take(20);
        var queries = new[]
        {
            inactive.ToQueryString(), expired.ToQueryString(), notExpiredIdle.ToQueryString(),
            aboutToExpire.ToQueryString(), ageOrder.ToQueryString(), bytes.ToQueryString(), recent.ToQueryString()
        };
        foreach (var sql in queries)
        {
            Require(!sql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase),
                "Una consulta de monitoreo requiere OPENJSON y no es compatible con SQL Server 2014.");
            Require(sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase), "No se tradujo la consulta de monitoreo.");
        }
        Require(queries[4].Contains("CASE", StringComparison.OrdinalIgnoreCase) && queries[4].Contains("ORDER BY", StringComparison.OrdinalIgnoreCase),
            "No se tradujo el orden por actividad/creación legada.");
        Require(queries[5].Contains("SUM", StringComparison.OrdinalIgnoreCase), "La suma de bytes no se tradujo a SQL.");
    }
}

static void TestHealthExplanations()
{
    var healthy = new ServerHealthDto { IsHealthy = true };
    var clear = OperationalHealthCalculator.Explain(healthy, 0, 0, 0);
    Require(OperationalHealthCalculator.Score(clear) == 100, "Un servidor sin incidencias no obtiene 100.");
    var worst = OperationalHealthCalculator.Explain(new ServerHealthDto { IsHealthy = false, Message = "SQL no disponible" }, 100, 100, 100);
    Require(OperationalHealthCalculator.Score(worst) == 10, "La fórmula no respeta los topes 25/30/20/15.");
    Require(worst.Select(x => x.Penalty).SequenceEqual(new double[] { 25, 30, 20, 15 }), "Los componentes del índice no coinciden con lo explicado al operador.");
    Require(worst.All(x => !string.IsNullOrWhiteSpace(x.Label) && !string.IsNullOrWhiteSpace(x.Detail) && !string.IsNullOrWhiteSpace(x.Remediation)),
        "Un componente no explica la condición y la acción de recuperación.");
    Require(OperationalHealthCalculator.Score(OperationalHealthCalculator.Explain(healthy, 10, 2, 1)) == 81,
        "La deducción proporcional de fallas/inactividad/vencimiento es incorrecta.");

    var storageFailure = OperationalHealthCalculator.Explain(new ServerHealthDto
    {
        IsHealthy = false, DatabaseOk = true, MessageBusOk = true, AlertingOk = true,
        StorageOk = false,
        StorageDiagnostic = new StorageHealthDiagnosticDto
        {
            Code = "StorageAccessDenied", Message = "La eliminación del archivo de prueba fue denegada.",
            Remediation = "Conceda modificación a la cuenta ASE\\servicio sobre la carpeta compartida."
        }
    }, 0, 0, 0).Single(component => component.Key == "dependencies");
    Require(storageFailure.Detail.Contains("eliminación del archivo de prueba") &&
            storageFailure.Remediation.Contains("ASE\\servicio") &&
            !storageFailure.Remediation.Contains("SMTP"),
        "El diagnóstico de una falla de almacenamiento no conserva causa y reparación específicas.");
}

static async Task TestAdministrativeContractAsync()
{
    Require(typeof(MonitoringController).GetCustomAttributes<AuthorizeAttribute>().Any(x => x.Policy == "PrivilegedEfirma"),
        "El resumen de monitoreo no exige e.firma privilegiada.");
    Require(typeof(MonitoringStatisticsController).GetCustomAttributes<AuthorizeAttribute>().Any(x => x.Policy == "PrivilegedEfirma"),
        "El reinicio de estadísticas no exige e.firma privilegiada.");
    Require(typeof(MonitoringStatisticsController).GetMethod(nameof(MonitoringStatisticsController.Reset))!
            .GetCustomAttributes<AuthorizeAttribute>().Any(x => x.Policy == "AdminEfirma"),
        "La mutación de estadísticas no exige rol administrador.");
    Require(typeof(MonitoringHealthController).GetCustomAttributes<AuthorizeAttribute>().Any(x => x.Policy == "AdminEfirma"),
        "La comprobación forzada de salud no exige e.firma de administrador.");
    var healthController = new MonitoringHealthController(new HealthyServer());
    var checkedHealth = await healthController.Recheck(default);
    Require((checkedHealth.Result as OkObjectResult)?.Value is ServerHealthDto { IsHealthy: true },
        "La acción de revalidar no devuelve la nueva comprobación al Dashboard.");
    var factory = new TestDbFactory(new DbContextOptionsBuilder<FileXferDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    var controller = new MonitoringStatisticsController(new MonitoringStatisticsService(factory))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
    Require(await controller.Reset(new("Prueba sin actor"), default) is ForbidResult, "El reinicio aceptó una solicitud sin RFC responsable.");
    controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("rfc", "CARJ721209SD2") }, "test"));
    Require(await controller.Reset(new("abc"), default) is BadRequestObjectResult, "El controlador aceptó un motivo demasiado corto.");
    await using var db = factory.CreateDbContext();
    Require(await db.AuditLogs.CountAsync() == 0, "Una solicitud inválida alteró el estado de estadísticas.");
}

static UploadEntry Upload(string status, DateTimeOffset activityUtc, long bytes) => new()
{
    Id = Guid.NewGuid(), FileUuid = Guid.NewGuid().ToString("N"), Rfc = "CARJ721209SD2",
    OriginalFileName = "documento.pdf", StoredRelativePath = "test/partial", Status = status,
    CreatedUtc = activityUtc, LastActivityUtc = activityUtc, BytesReceived = bytes,
    VerificationRequired = true, VerificationPerformed = status == "Completed"
};

static async Task<SummaryResponse> SummaryAsync(MonitoringController controller)
{
    var result = await controller.GetSummary(default);
    return (result.Result as OkObjectResult)?.Value as SummaryResponse
        ?? throw new InvalidOperationException("El resumen no respondió HTTP 200 con su contrato.");
}

static async Task ExpectArgumentAsync(Func<Task<DateTimeOffset>> action)
{
    try { await action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("Se aceptó un motivo de reinicio inválido.");
}

static async Task TestDashboardCanonicalPathAsync()
{
    var nextCalls = 0;
    var middleware = new AdminDashboardCanonicalPathMiddleware(context =>
    {
        nextCalls++;
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    });

    foreach (var method in new[] { HttpMethods.Get, HttpMethods.Head })
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/admin";
        context.Request.PathBase = "/transfer";
        context.Request.QueryString = new QueryString("?view=pulse&filter=%2F");
        await middleware.InvokeAsync(context);
        Require(context.Response.StatusCode == StatusCodes.Status308PermanentRedirect &&
                context.Response.Headers.Location == "/transfer/admin/?view=pulse&filter=%2F" && nextCalls == 0,
            "La entrada al Dashboard no canonizó GET/HEAD preservando PathBase y query.");
    }

    foreach (var (method, path) in new[]
    {
        (HttpMethods.Get, "/admin/"),
        (HttpMethods.Get, "/admin/policies"),
        (HttpMethods.Get, "/admin/monitoring/summary"),
        (HttpMethods.Get, "/admin/assets/application.js"),
        (HttpMethods.Get, "/administrator"),
        (HttpMethods.Post, "/admin"),
        (HttpMethods.Put, "/admin"),
        (HttpMethods.Options, "/admin")
    })
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        Require(context.Response.StatusCode == StatusCodes.Status204NoContent && !context.Response.Headers.ContainsKey("Location"),
            $"La canonización interceptó indebidamente {method} {path}.");
    }
    Require(nextCalls == 8, "El middleware no continuó las rutas ajenas a la entrada exacta del Dashboard.");

    var upperCase = new DefaultHttpContext();
    upperCase.Request.Method = HttpMethods.Get;
    upperCase.Request.Path = "/ADMIN";
    await middleware.InvokeAsync(upperCase);
    Require(upperCase.Response.StatusCode == StatusCodes.Status308PermanentRedirect &&
            upperCase.Response.Headers.Location == "/admin/",
        "La entrada en mayúsculas no canonizó a la misma URL estable.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class TestDbFactory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
{
    public FileXferDbContext CreateDbContext() => new(options);
    public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
}

file sealed class HealthyServer : ISystemHealthService
{
    public Task<ServerHealthDto> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(new ServerHealthDto
    {
        IsHealthy = true, DatabaseOk = true, StorageOk = true, MessageBusOk = true, AlertingOk = true
    });
}
