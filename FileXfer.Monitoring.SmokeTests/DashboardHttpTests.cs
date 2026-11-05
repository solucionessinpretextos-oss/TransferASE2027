using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
