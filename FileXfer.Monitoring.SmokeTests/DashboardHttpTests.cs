using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FileXfer.Server.Controllers.Admin;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class DashboardHttpTests
{
    public static async Task RunAsync()
    {
        var factory = new Factory(new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("dashboard-http-" + Guid.NewGuid()).Options);
        var loginAlert = new AlertOutboxDelivery
        {
            EventId = Guid.NewGuid(), Transport = "email", Title = "Login SAT fallido",
            Message = "RFC=CARJ721209SD2; IP=192.0.2.1; Error=Cert fuera de vigencia; Password=NEVER-RETURN-THIS",
            Status = AlertOutboxStatuses.DeadLetter, AttemptCount = 3, LastFailureCode = "SmtpException",
            OccurredUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow
        };
        await using (var db = factory.CreateDbContext())
        {
            db.AlertOutboxDeliveries.Add(loginAlert);
            await db.SaveChangesAsync();
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDbContextFactory<FileXferDbContext>>(factory);
        builder.Services.AddSingleton<MonitoringStatisticsService>();
        builder.Services.AddSingleton<AlertStatisticsService>();
        builder.Services.AddSingleton<IFilePolicyService, FilePolicyService>();
        builder.Services.Configure<AlertOptions>(options => {
            options.Enabled = true; options.SmtpHost = "smtp.dev.tu-dominio.com";
            options.EmailFrom = "sender@example.invalid"; options.EmailTo = "receiver@example.invalid";
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(MonitoringStatisticsController).Assembly);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, LocalTestAuth>("Test", _ => { });
        builder.Services.AddAuthorization(DashboardAuthorization.Configure);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpResponseMessage> Request(string method, string path, string? role, object? body = null, bool efirma = true)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (role is not null) request.Headers.Add("Test-Role", role);
                if (efirma) request.Headers.Add("Test-Amr", "efirma");
                if (body is not null) request.Content = JsonContent.Create(body);
                return await client.SendAsync(request);
            }
            var writes = new[] {
                ("PUT", "/admin/file-policies/.pdf"), ("POST", "/admin/file-policies/rollback"),
                ("POST", "/admin/monitoring/pending/remove"), ("POST", "/admin/monitoring/pending/cleanup-expired"),
                ("POST", "/admin/monitoring/pending/reconcile"), ("POST", "/admin/monitoring/statistics/reset"),
                ("POST", "/admin/monitoring/health/recheck"), ("POST", "/admin/rfcs"),
                ("PUT", "/admin/rfcs/CARJ721209SD2"), ("POST", "/admin/rfcs/CARJ721209SD2/activate"),
                ("POST", "/admin/rfcs/CARJ721209SD2/deactivate")
            };
            foreach (var (method, path) in writes)
            {
                using var response = await Request(method, path, "ops", new { reason = "Pruebas" });
                Check(response.StatusCode == HttpStatusCode.Forbidden, $"Solo consulta pudo alcanzar {method} {path}: {response.StatusCode}");
            }
            using (var response = await Request("GET", "/admin/rfcs", "ops"))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "Consulta accedió al directorio administrativo de RFC.");
            foreach (var (role, efirma, expected) in new[] { ((string?)null, true, 401), ("user", true, 403), ("admin", false, 403) })
            {
                using var response = await Request("POST", "/admin/monitoring/statistics/reset", role, new { reason = "Pruebas" }, efirma);
                Check((int)response.StatusCode == expected, "Se omitió autenticación, rol o e.firma al reiniciar.");
            }
            foreach (var body in new object[] { new { reason = "" }, new { reason = "abcd" }, new { reason = "     " }, new { reason = new string('a', 501) }, new { }, new { reason = (string?)null } })
            {
                using var response = await Request("POST", "/admin/monitoring/statistics/reset", "admin", body);
                Check(response.StatusCode == HttpStatusCode.BadRequest, $"Motivo inválido debe devolver 400, devolvió {(int)response.StatusCode}.");
            }
            using (var response = await Request("POST", "/admin/monitoring/statistics/reset", "admin", new { reason = "Pruebas" }))
            {
                Check(response.IsSuccessStatusCode, $"Reinicio MVC real falló: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                Check((await response.Content.ReadAsStringAsync()).Contains("statisticsStartedUtc"), "Falta nueva fecha del período.");
            }
            await using (var db = factory.CreateDbContext())
            {
                var audit = await db.AuditLogs.SingleAsync();
                Check(audit.Action == MonitoringStatisticsService.ResetAction && audit.ActorRfc == "CARJ721209SD2" && audit.Details!.Contains("Pruebas"), "Falta auditoría del reinicio HTTP.");
                Check(await db.AlertOutboxDeliveries.CountAsync() == 1, "Reiniciar borró evidencia de alertas.");
            }
            using (var response = await Request("GET", "/admin/file-policies", "ops"))
                Check(response.IsSuccessStatusCode, "Solo consulta debe poder leer políticas.");
            using (var response = await Request("GET", "/admin/monitoring/alerts", "ops"))
            {
                Check(response.IsSuccessStatusCode, "Solo consulta debe poder consultar entregas.");
                var raw = await response.Content.ReadAsStringAsync();
                Check(!raw.Contains("NEVER-RETURN-THIS") && !raw.Contains("192.0.2.1"), "La API expuso el cuerpo interno de la alerta.");
                var page = await response.Content.ReadFromJsonAsync<AlertDeliveriesController.PageDto>();
                Check(page!.TotalCount == 1 && page.Items[0].FileId is null && page.Items[0].RelatedItem.Contains("sin archivo"), "Login confundido con falla de un archivo.");
                Check(page.Items[0].FailedAttempts == 3 && page.ConfigurationIssues.Length == 1 && !page.WebhookConfigured, "Detalle de intentos o configuración incorrecto.");
            }
            foreach (var query in new[] { "page=0", "pageSize=101", "status=Invalid" })
            {
                using var response = await Request("GET", "/admin/monitoring/alerts?" + query, "admin");
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Falta validar filtro/paginación de alertas.");
            }
            using (var response = await Request("GET", "/admin/monitoring/alerts?status=Delivered", "ops"))
                Check((await response.Content.ReadFromJsonAsync<AlertDeliveriesController.PageDto>())!.TotalCount == 0, "Filtro de entregadas ignorado.");
            var fileAlert = new AlertOutboxDelivery { Title = "Descarga bloqueada", Message = "ArchivoLocalId=51;RFC=CARJ721209SD2;TraceId=abc-123;FileNameEncoded=" + Uri.EscapeDataString("Informe; revisión.pdf") };
            var detail = AlertDeliveriesController.Describe(fileAlert);
            Check(detail.FileId == 51 && detail.FileName == "Informe; revisión.pdf" && detail.TraceId == "abc-123", "Se perdió la asociación de la alerta con el archivo.");
            Check(EmailAlertSink.SocketOptionsForPort(465) == MailKit.Security.SecureSocketOptions.SslOnConnect &&
                  EmailAlertSink.SocketOptionsForPort(587) == MailKit.Security.SecureSocketOptions.StartTls &&
                  EmailAlertSink.SocketOptionsForPort(25) == MailKit.Security.SecureSocketOptions.StartTls, "Se degradó TLS o no se soporta 465.");
            Check(AlertFailureDescription.Code(new MailKit.Security.AuthenticationException("secret")) == "smtp_authentication_failed", "No se clasificó SMTP sin exponer credenciales.");
            Console.WriteLine("PASS dashboard-http ops=11-writes-denied rfc-directory=admin-only efirma=required reset=200 invalid-reasons=400 reset-audit=preserved alerts=context-and-pagination-secret-safe smtp465=implicit-tls");

            await VerifyAlertStatisticsAsync();

            async Task VerifyAlertStatisticsAsync()
            {
                const string alertsRoute = "/admin/monitoring/alerts";
                const string statisticsRoute = alertsRoute + "/statistics";
                const string resetRoute = statisticsRoute + "/reset";
                const string actorRfc = "CARJ721209SD2";
                const string firstReason = "Canal SMTP verificado";
                const string secondReason = "Cierre del período siguiente";

                foreach (var (role, efirma, expected) in new[]
                {
                    ((string?)null, true, HttpStatusCode.Unauthorized),
                    ("ops", true, HttpStatusCode.Forbidden),
                    ("admin", false, HttpStatusCode.Forbidden)
                })
                {
                    using var read = await Request("GET", statisticsRoute, role, efirma: efirma);
                    using var write = await Request("POST", resetRoute, role, new { reason = firstReason }, efirma);
                    Check(read.StatusCode == expected && write.StatusCode == expected,
                        "La bitácora y el reinicio de alertas requieren administrador con e.firma.");
                }

                foreach (var invalid in new[] { "", "  abcd  ", new string('a', 501) })
                {
                    using var response = await Request("POST", resetRoute, "admin", new { reason = invalid });
                    Check(response.StatusCode == HttpStatusCode.BadRequest, "Se aceptó un motivo inválido al reiniciar alertas.");
                }

                var pendingAlert = new AlertOutboxDelivery
                {
                    EventId = Guid.NewGuid(), Transport = "email", Title = "Aviso previo todavía pendiente",
                    Message = "Evento sintético de prueba",
                    Status = AlertOutboxStatuses.Pending, AttemptCount = 1, LastFailureCode = "SmtpException",
                    OccurredUtc = loginAlert.OccurredUtc, UpdatedUtc = loginAlert.UpdatedUtc,
                    NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                };
                await using (var db = factory.CreateDbContext())
                {
                    db.AlertOutboxDeliveries.Add(pendingAlert);
                    await db.SaveChangesAsync();
                }

                var before = await GetStatisticsAsync("?page=1&pageSize=10");
                Check(before.GetProperty("statisticsStartedUtc").ValueKind == JsonValueKind.Null &&
                      before.GetProperty("totalCount").GetInt32() == 0, "La bitácora parte con un reinicio inexistente.");
                var reset = await ResetAlertStatisticsAsync("  " + firstReason + "  ");
                var cutoff = reset.GetProperty("statisticsStartedUtc").GetDateTimeOffset();
                Check(reset.GetProperty("exhaustedBefore").GetInt32() == 1, "El reinicio no informó el agotado del período anterior.");

                var current = await GetDeliveriesAsync("?period=Current&status=All");
                Check(current.StatisticsStartedUtc == cutoff && current.TotalCount == 1 &&
                      current.Items[0].Id == pendingAlert.Id && current.Items[0].CountsInCurrentPeriod && current.Items[0].FailedAttempts == 1,
                    "El período nuevo ocultó el pendiente previo o cambió sus intentos.");
                var history = await GetDeliveriesAsync("");
                Check(history.StatisticsStartedUtc == cutoff && history.TotalCount == 1 && history.Items[0].Id == loginAlert.Id &&
                      !history.Items[0].CountsInCurrentPeriod && history.Items[0].Status == AlertOutboxStatuses.DeadLetter &&
                      history.Items[0].FailedAttempts == 3 && history.Items[0].FailureCode == "SmtpException",
                    "La consulta compatible perdió el agotado histórico o cambió su evidencia.");

                await using (var db = factory.CreateDbContext())
                {
                    var audit = await db.AuditLogs.SingleAsync(row => row.Action == AlertStatisticsService.ResetAction);
                    Check(audit.ActorRfc == actorRfc && audit.Ip == "127.0.0.1" && audit.TimestampUtc == cutoff && audit.Details!.Contains(firstReason),
                        "El reinicio de alertas no dejó un corte con autor, origen y motivo en la base.");
                    var pending = await db.AlertOutboxDeliveries.SingleAsync(row => row.Id == pendingAlert.Id);
                    Check(pending.Status == AlertOutboxStatuses.Pending && pending.AttemptCount == 1 && pending.NextAttemptUtc == pendingAlert.NextAttemptUtc,
                        "El reinicio modificó la programación de un aviso pendiente.");
                    Check(await db.AlertOutboxDeliveries.CountAsync() == 2, "El reinicio eliminó entregas.");
                    // Una alerta que nació antes del corte puede agotar sus intentos dentro del nuevo período.
                    pending.Status = AlertOutboxStatuses.DeadLetter;
                    pending.AttemptCount = 3;
                    pending.UpdatedUtc = cutoff;
                    await db.SaveChangesAsync();
                }

                current = await GetDeliveriesAsync("?period=Current&status=DeadLetter");
                Check(current.TotalCount == 1 && current.Items[0].Id == pendingAlert.Id && current.Items[0].CountsInCurrentPeriod,
                    "Un agotado nuevo que nació antes del corte no volvió a contar.");
                var nextReset = await ResetAlertStatisticsAsync(secondReason);
                var nextCutoff = nextReset.GetProperty("statisticsStartedUtc").GetDateTimeOffset();
                Check(nextCutoff > cutoff && nextReset.GetProperty("exhaustedBefore").GetInt32() == 1,
                    "El reinicio siguiente acumuló agotados históricos o no avanzó el corte.");

                var latest = await GetStatisticsAsync("?page=1&pageSize=1");
                var oldest = await GetStatisticsAsync("?page=2&pageSize=1");
                var newestEntry = latest.GetProperty("items")[0];
                var oldestEntry = oldest.GetProperty("items")[0];
                Check(latest.GetProperty("page").GetInt32() == 1 && latest.GetProperty("pageSize").GetInt32() == 1 &&
                      latest.GetProperty("statisticsStartedUtc").GetDateTimeOffset() == nextCutoff && latest.GetProperty("totalCount").GetInt32() == 2 &&
                      oldest.GetProperty("page").GetInt32() == 2,
                    "La bitácora no informó el corte activo o su paginación.");
                Check(newestEntry.GetProperty("id").GetGuid() != Guid.Empty && newestEntry.GetProperty("actorRfc").GetString() == actorRfc &&
                      newestEntry.GetProperty("reason").GetString() == secondReason && newestEntry.GetProperty("timestampUtc").GetDateTimeOffset() == nextCutoff &&
                      newestEntry.GetProperty("previousStartedUtc").GetDateTimeOffset() == cutoff && newestEntry.GetProperty("exhaustedBefore").GetInt32() == 1,
                    "La bitácora no mostró los datos del último corte primero.");
                Check(oldestEntry.GetProperty("reason").GetString() == firstReason &&
                      oldestEntry.GetProperty("previousStartedUtc").ValueKind == JsonValueKind.Null,
                    "La segunda página perdió el primer corte o su motivo.");

                const string legacyAuditRoute = "/audit/logs";
                using (var legacy = await Request("GET", legacyAuditRoute, "ops"))
                {
                    legacy.EnsureSuccessStatusCode();
                    var payload = await legacy.Content.ReadFromJsonAsync<JsonElement>();
                    Check(payload.GetProperty("total").GetInt32() == 1 && payload.GetProperty("items").GetArrayLength() == 1 &&
                          payload.GetProperty("items")[0].GetProperty("action").GetString() == MonitoringStatisticsService.ResetAction,
                        "El endpoint legado expuso la bitácora de alertas u ocultó otra acción existente.");
                }
                var resetAuditRoute = legacyAuditRoute + "?action=" + Uri.EscapeDataString(AlertStatisticsService.ResetAction);
                foreach (var (role, count) in new[] { ("ops", 0), ("admin", 2) })
                {
                    using var legacy = await Request("GET", resetAuditRoute, role);
                    legacy.EnsureSuccessStatusCode();
                    var payload = await legacy.Content.ReadFromJsonAsync<JsonElement>();
                    Check(payload.GetProperty("total").GetInt32() == count && payload.GetProperty("items").GetArrayLength() == count,
                        $"La auditoría legada aplicó visibilidad incorrecta para el rol {role}.");
                }

                using var invalidPeriod = await Request("GET", alertsRoute + "?period=Invalid", "ops");
                Check(invalidPeriod.StatusCode == HttpStatusCode.BadRequest, "Se aceptó un período de alertas inválido.");
                Console.WriteLine("PASS alert-statistics-http admin-efirma=required reason=required audit=paged legacy-audit=admin-only original-delivery=preserved pending=visible new-exhaustion=counted");

                async Task<JsonElement> GetStatisticsAsync(string query)
                {
                    using var response = await Request("GET", statisticsRoute + query, "admin");
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadFromJsonAsync<JsonElement>();
                }

                async Task<JsonElement> ResetAlertStatisticsAsync(string reason)
                {
                    using var response = await Request("POST", resetRoute, "admin", new { reason });
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadFromJsonAsync<JsonElement>();
                }

                async Task<AlertDeliveriesController.PageDto> GetDeliveriesAsync(string query)
                {
                    using var response = await Request("GET", alertsRoute + query, "ops");
                    response.EnsureSuccessStatusCode();
                    return (await response.Content.ReadFromJsonAsync<AlertDeliveriesController.PageDto>())!;
                }
            }
        }
        finally { await app.StopAsync(); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Factory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
    { public FileXferDbContext CreateDbContext() => new(options); }
}

// Este esquema solo existe en el ejecutable de pruebas, nunca en el servidor publicado.
file sealed class LocalTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()), new("rfc", "CARJ721209SD2") };
        if (Request.Headers["Test-Amr"] == "efirma") claims.Add(new("amr", "efirma"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}
