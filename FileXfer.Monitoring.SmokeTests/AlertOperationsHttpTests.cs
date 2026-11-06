using System.Collections.Concurrent;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class AlertOperationsHttpTests
{
    private const string Actor = "AUDI000101ABC";
    private const string AlertsPath = "/admin/monitoring/alerts";

    public static async Task RunAsync()
    {
        var factory = new Factory(new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("alert-operations-http-" + Guid.NewGuid()).Options);
        var original = new AlertOutboxDelivery
        {
            EventId = Guid.NewGuid(), Transport = "email", Title = "Alerta de ejemplo",
            Message = "Detalle interno reservado", Status = AlertOutboxStatuses.DeadLetter,
            LastFailureCode = "smtp_535", AttemptCount = 3, CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1), OccurredUtc = DateTimeOffset.UtcNow.AddDays(-1)
        };
        await using (var db = factory.CreateDbContext())
        {
            db.AlertOutboxDeliveries.Add(original);
            await db.SaveChangesAsync();
        }

        var email = new FakeTransport("email", true, AlertTransportResult.Delivered());
        var webhook = new FakeTransport("webhook", false, AlertTransportResult.Failed("http_403"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDbContextFactory<FileXferDbContext>>(factory);
        builder.Services.Configure<AlertOptions>(options =>
        {
            options.Enabled = true;
            options.WorkerCount = 1;
            options.PollIntervalMilliseconds = 50;
            options.MaxDeliveryAttempts = 2;
            options.RetryInitialDelayMilliseconds = 20;
        });
        builder.Services.AddSingleton<IAlertTransport>(email);
        builder.Services.AddSingleton<IAlertTransport>(webhook);
        builder.Services.AddSingleton<LogAlertSink>();
        builder.Services.AddSingleton<AlertOperationsService>();
        builder.Services.AddSingleton<IHostedService, AlertDeliveryQueue>();
        builder.Services.AddControllers().AddApplicationPart(typeof(AlertOperationsController).Assembly);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, OperationsAuth>("Test", _ => { });
        builder.Services.AddAuthorization(DashboardAuthorization.Configure);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var testPath = AlertsPath + "/test";
            var reprocessPath = $"{AlertsPath}/{original.Id}/reprocess";
            var testId = Guid.NewGuid();
            var testBody = new { channel = "email", requestId = testId, recipient = "unexpected@example.invalid" };
            var reprocessId = Guid.NewGuid();
            var reprocessBody = new { reason = "   Se recuperó SMTP   ", requestId = reprocessId };
            foreach (var (method, path, body) in new[]
            {
                (HttpMethod.Post, testPath, (object?)testBody),
                (HttpMethod.Post, reprocessPath, (object?)reprocessBody),
                (HttpMethod.Get, $"{AlertsPath}/{original.Id}", null)
            })
            {
                using var unauthorized = await SendAsync(method, path, body, null);
                using var readOnly = await SendAsync(method, path, body, "ops");
                using var wrongAmr = await SendAsync(method, path, body, "admin", efirma: false);
                Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized &&
                      readOnly.StatusCode == HttpStatusCode.Forbidden && wrongAmr.StatusCode == HttpStatusCode.Forbidden,
                    "La operación permitió una sesión sin administrador y e.firma.");
            }
            using (var notConfigured = await SendAsync(HttpMethod.Post, testPath,
                       new { channel = "webhook", requestId = Guid.NewGuid() }))
                Check(notConfigured.StatusCode == HttpStatusCode.Conflict, "Aceptó un canal sin configurar.");
            using (var arbitrary = await SendAsync(HttpMethod.Post, testPath,
                       new { channel = "https://unexpected.example.invalid", requestId = Guid.NewGuid() }))
                Check(arbitrary.StatusCode == HttpStatusCode.BadRequest, "Aceptó un canal arbitrario.");
            using (var missingReason = await SendAsync(HttpMethod.Post, reprocessPath,
                       new { reason = "    ", requestId = reprocessId }))
                Check(missingReason.StatusCode == HttpStatusCode.BadRequest, "Aceptó un reproceso sin motivo.");
            using (var oversizedAudit = await SendAsync(HttpMethod.Post, reprocessPath,
                       new { reason = new string('\u0001', 500), requestId = reprocessId }))
                Check(oversizedAudit.StatusCode == HttpStatusCode.BadRequest, "Aceptó un motivo que desborda la bitácora.");

            await AssertAcceptedAsync(testPath, testBody, testId, testId);
            await AssertAcceptedAsync(testPath, testBody, testId, testId);
            using (var limited = await SendAsync(HttpMethod.Post, testPath, new { channel = "email", requestId = Guid.NewGuid() }))
                Check(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter?.Delta > TimeSpan.Zero,
                    "No limitó la frecuencia de prueba por canal.");
            var testDetail = await WaitForStatusAsync(testId, AlertOutboxStatuses.Delivered);
            Check(testDetail.GetProperty("deliveredUtc").ValueKind == JsonValueKind.String && email.Messages.Count == 1,
                "La prueba de canal no mostró la aceptación o se duplicó.");

            await AssertAcceptedAsync(reprocessPath, reprocessBody, reprocessId, original.EventId);
            await AssertAcceptedAsync(reprocessPath, reprocessBody, reprocessId, original.EventId);
            using (var repeated = await SendAsync(HttpMethod.Post, reprocessPath,
                       new { reason = "Segundo clic", requestId = Guid.NewGuid() }))
            {
                var body = await repeated.Content.ReadFromJsonAsync<JsonElement>();
                Check(repeated.StatusCode == HttpStatusCode.Conflict && body.GetProperty("deliveryId").GetGuid() == reprocessId,
                    "Un identificador distinto duplicó el reproceso o perdió su vínculo.");
            }
            var detail = await WaitForStatusAsync(reprocessId, AlertOutboxStatuses.Delivered);
            Check(!detail.ToString().Contains(original.Message) &&
                  email.Messages.Count(message => message.EventId == original.EventId && message.Message == original.Message) == 1,
                "El reproceso perdió el contenido original, lo duplicó o lo expuso en HTTP.");
            using (var activeSource = await SendAsync(HttpMethod.Post, $"{AlertsPath}/{reprocessId}/reprocess",
                       new { reason = "Ya fue entregado", requestId = Guid.NewGuid() }))
                Check(activeSource.StatusCode == HttpStatusCode.Conflict, "Aceptó reprocesar una entrega confirmada.");

            webhook.IsConfigured = true;
            var webhookTestId = Guid.NewGuid();
            await AssertAcceptedAsync(testPath, new { channel = "webhook", requestId = webhookTestId }, webhookTestId, webhookTestId);
            var failed = await WaitForStatusAsync(webhookTestId, AlertOutboxStatuses.DeadLetter);
            Check(failed.GetProperty("failureCode").GetString() == "http_403" &&
                  failed.GetProperty("failedAttempts").GetInt32() == 2 && webhook.Messages.Count == 2,
                "No se mostró el fallo simulado del canal o se alteraron los reintentos del outbox.");

            await using var inspection = factory.CreateDbContext();
            var storedOriginal = await inspection.AlertOutboxDeliveries.SingleAsync(row => row.Id == original.Id);
            Check(storedOriginal.Status == original.Status && storedOriginal.UpdatedUtc == original.UpdatedUtc &&
                  storedOriginal.AttemptCount == original.AttemptCount && storedOriginal.LastFailureCode == original.LastFailureCode,
                "El reproceso modificó el aviso de origen.");
            var audit = await inspection.AuditLogs.OrderBy(row => row.TimestampUtc).ToArrayAsync();
            Check(audit.Length == 3 && audit.All(row => row.ActorRfc == Actor && row.Ip == "127.0.0.1"),
                "La bitácora perdió el actor, IP o duplicó solicitudes.");
            var reprocessed = audit.Single(row => row.Action == AlertOperationsService.ReprocessAction);
            using var details = JsonDocument.Parse(reprocessed.Details!);
            using var failedDetails = JsonDocument.Parse(audit.Single(row => row.Id == webhookTestId).Details!);
            Check(details.RootElement.GetProperty("sourceDeliveryId").GetGuid() == original.Id &&
                  details.RootElement.GetProperty("deliveryId").GetGuid() == reprocessId &&
                  details.RootElement.GetProperty("reason").GetString() == "Se recuperó SMTP" &&
                  details.RootElement.GetProperty("outcomeStatus").GetString() == AlertOutboxStatuses.Delivered &&
                  details.RootElement.GetProperty("outcomeUtc").GetDateTimeOffset().Offset == TimeSpan.Zero &&
                  failedDetails.RootElement.GetProperty("outcomeStatus").GetString() == AlertOutboxStatuses.DeadLetter &&
                  failedDetails.RootElement.GetProperty("outcomeFailureCode").GetString() == "http_403" &&
                  failedDetails.RootElement.GetProperty("failedAttempts").GetInt32() == 2 &&
                  audit.Count(row => row.Action == AlertOperationsService.TestAction) == 2,
                "Faltan los vínculos, motivo o resultado final en auditoría.");

            inspection.AlertOutboxDeliveries.Remove(await inspection.AlertOutboxDeliveries.SingleAsync(row => row.Id == reprocessId));
            await inspection.SaveChangesAsync();
            using (var archivedReplay = await SendAsync(HttpMethod.Post, reprocessPath, reprocessBody))
                Check(archivedReplay.StatusCode == HttpStatusCode.Gone, "Reactivó una solicitud archivada.");
            using (var archivedSource = await SendAsync(HttpMethod.Post, reprocessPath,
                       new { reason = "Otra solicitud", requestId = Guid.NewGuid() }))
                Check(archivedSource.StatusCode == HttpStatusCode.Conflict, "El vencimiento permitió duplicar el reproceso.");
            Console.WriteLine("PASS alert-operations-http test=fixed-destination-and-limited reprocess=idempotent-and-preserves-origin auth=admin-efirma audit=durable-outcome transport=simulated");

            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? role = "admin",
                bool efirma = true)
            {
                using var request = new HttpRequestMessage(method, path);
                if (role is not null) request.Headers.Add("Test-Role", role);
                if (efirma) request.Headers.Add("Test-Amr", "efirma");
                if (body is not null) request.Content = JsonContent.Create(body);
                return await client.SendAsync(request);
            }

            async Task AssertAcceptedAsync(string path, object body, Guid deliveryId, Guid eventId)
            {
                using var response = await SendAsync(HttpMethod.Post, path, body);
                var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
                Check(response.StatusCode == HttpStatusCode.Accepted && response.Headers.Location?.ToString().EndsWith(deliveryId.ToString()) == true &&
                      receipt.GetProperty("deliveryId").GetGuid() == deliveryId && receipt.GetProperty("eventId").GetGuid() == eventId,
                    $"La solicitud no confirmó la entrega esperada: HTTP {(int)response.StatusCode}.");
            }

            async Task<JsonElement> WaitForStatusAsync(Guid id, string status)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    using var response = await SendAsync(HttpMethod.Get, $"{AlertsPath}/{id}", null);
                    response.EnsureSuccessStatusCode();
                    var delivery = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (delivery.GetProperty("status").GetString() == status) return delivery;
                    await Task.Delay(30);
                }
                throw new InvalidOperationException("El worker simulado no alcanzó el estado esperado.");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Factory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
    {
        public FileXferDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeTransport(string name, bool configured, AlertTransportResult result) : IAlertTransport
    {
        public string Name => name;
        public bool IsConfigured { get; set; } = configured;
        public ConcurrentQueue<SecurityAlertEnvelope> Messages { get; } = new();

        public Task<AlertTransportResult> SendAsync(SecurityAlertEnvelope alert, CancellationToken ct)
        {
            Messages.Enqueue(alert);
            return Task.FromResult(result);
        }
    }

    private sealed class OperationsAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Test-Role", out var role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()), new("rfc", Actor) };
            if (Request.Headers["Test-Amr"] == "efirma") claims.Add(new("amr", "efirma"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }
}
