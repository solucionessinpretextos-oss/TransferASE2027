// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/Alerting.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Net;
using System.Net.Mail;
using System.Text;
using FileXfer.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public interface IAlertSink
{
    Task NotifySecurityEventAsync(string title, string message, CancellationToken ct);
}

public sealed record SecurityAlertEnvelope(
    Guid EventId,
    string Title,
    string Message,
    DateTimeOffset OccurredUtc);

public readonly record struct AlertTransportResult(bool Success, string? FailureCode)
{
    public static AlertTransportResult Delivered() => new(true, null);
    public static AlertTransportResult Failed(string code) => new(false, code);
}

public interface IAlertTransport
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<AlertTransportResult> SendAsync(SecurityAlertEnvelope alert, CancellationToken ct);
}

public sealed class LogAlertSink : IAlertSink
{
    private readonly ILogger<LogAlertSink> _log;
    public LogAlertSink(ILogger<LogAlertSink> log) => _log = log;
    public Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        _log.LogWarning(
            "ALERTA-SEGURIDAD: {Title} :: {Message}",
            NormalizeTitle(title),
            Limit(message, 16_384));
        return Task.CompletedTask;
    }

    internal static string NormalizeTitle(string title) =>
        Limit(title.Replace('\r', ' ').Replace('\n', ' '), 200);

    internal static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

public sealed class EmailAlertSink : IAlertTransport
{
    private readonly AlertOptions _opt;
    public EmailAlertSink(IOptions<AlertOptions> opt) => _opt = opt.Value;

    public string Name => "email";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.EmailTo) &&
        !string.IsNullOrWhiteSpace(_opt.SmtpHost) &&
        !string.IsNullOrWhiteSpace(_opt.EmailFrom);

    public async Task<AlertTransportResult> SendAsync(
        SecurityAlertEnvelope alert,
        CancellationToken ct)
    {
        using var smtp = new SmtpClient(_opt.SmtpHost!, _opt.SmtpPort)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = true,
            UseDefaultCredentials = false,
            Timeout = checked(_opt.SmtpTimeoutSeconds * 1000)
        };
        if (!string.IsNullOrWhiteSpace(_opt.SmtpUser))
            smtp.Credentials = new NetworkCredential(_opt.SmtpUser, _opt.SmtpPassword);

        using var mail = new MailMessage
        {
            From = new MailAddress(_opt.EmailFrom!),
            Subject = $"[ALERTA] {LogAlertSink.NormalizeTitle(alert.Title)}",
            SubjectEncoding = Encoding.UTF8,
            Body = LogAlertSink.Limit(alert.Message, 16_384),
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        foreach (var to in _opt.EmailTo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            mail.To.Add(to);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opt.SmtpTimeoutSeconds));
        await smtp.SendMailAsync(mail, timeout.Token);
        return AlertTransportResult.Delivered();
    }
}

public sealed class WebhookAlertSink : IAlertTransport
{
    public const string HttpClientName = "SecurityAlerts";

    private readonly AlertOptions _opt;
    private readonly IHttpClientFactory _httpClientFactory;

    public WebhookAlertSink(
        IOptions<AlertOptions> opt,
        IHttpClientFactory httpClientFactory)
    {
        _opt = opt.Value;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "webhook";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.WebhookUrl);

    public async Task<AlertTransportResult> SendAsync(
        SecurityAlertEnvelope alert,
        CancellationToken ct)
    {
        var payload = new
        {
            text = LogAlertSink.Limit($"[ALERTA] {alert.Title}\n{alert.Message}", 16_384)
        };
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _opt.WebhookUrl)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", alert.EventId.ToString("N"));
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        return response.IsSuccessStatusCode
            ? AlertTransportResult.Delivered()
            : AlertTransportResult.Failed($"http_{(int)response.StatusCode}");
    }
}
