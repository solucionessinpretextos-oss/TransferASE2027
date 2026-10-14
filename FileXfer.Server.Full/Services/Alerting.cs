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

public sealed class CompositeAlertSink : IAlertSink
{
    private readonly IEnumerable<IAlertSink> _sinks;
    private readonly ILogger<CompositeAlertSink> _log;

    public CompositeAlertSink(IEnumerable<IAlertSink> sinks, ILogger<CompositeAlertSink> log)
    {
        _sinks = sinks;
        _log = log;
    }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.NotifySecurityEventAsync(title, message, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "El canal de alerta {AlertSink} produjo un error no controlado",
                    sink.GetType().Name);
            }
        }
    }
}

public sealed class LogAlertSink : IAlertSink
{
    private readonly ILogger<LogAlertSink> _log;
    public LogAlertSink(ILogger<LogAlertSink> log) => _log = log;
    public Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        _log.LogWarning("ALERTA-SEGURIDAD: {Title} :: {Message}", title, message);
        return Task.CompletedTask;
    }
}

public sealed class EmailAlertSink : IAlertSink
{
    private readonly AlertOptions _opt;
    private readonly ILogger<EmailAlertSink> _log;
    public EmailAlertSink(IOptions<AlertOptions> opt, ILogger<EmailAlertSink> log) { _opt = opt.Value; _log = log; }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.EmailTo) || string.IsNullOrWhiteSpace(_opt.SmtpHost) || string.IsNullOrWhiteSpace(_opt.EmailFrom))
            return;

        try
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
                Subject = $"[ALERTA] {NormalizeTitle(title)}",
                SubjectEncoding = Encoding.UTF8,
                Body = Limit(message, 16_384),
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false
            };

            foreach (var to in _opt.EmailTo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mail.To.Add(to);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_opt.SmtpTimeoutSeconds));
            await smtp.SendMailAsync(mail, timeout.Token);
            _log.LogInformation("Alerta de seguridad enviada por email a {RecipientCount} destinatario(s)", mail.To.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fallo enviando alerta por email");
        }
    }

    private static string NormalizeTitle(string title) =>
        Limit(title.Replace('\r', ' ').Replace('\n', ' '), 200);

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

public sealed class WebhookAlertSink : IAlertSink
{
    public const string HttpClientName = "SecurityAlerts";

    private readonly AlertOptions _opt;
    private readonly ILogger<WebhookAlertSink> _log;
    private readonly IHttpClientFactory _httpClientFactory;

    public WebhookAlertSink(
        IOptions<AlertOptions> opt,
        ILogger<WebhookAlertSink> log,
        IHttpClientFactory httpClientFactory)
    {
        _opt = opt.Value;
        _log = log;
        _httpClientFactory = httpClientFactory;
    }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.WebhookUrl)) return;
        try
        {
            var payload = new { text = Limit($"[ALERTA] {title}\n{message}", 16_384) };
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var resp = await http.PostAsJsonAsync(_opt.WebhookUrl!, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError(
                    "El webhook de alertas respondió con HTTP {StatusCode}",
                    (int)resp.StatusCode);
                return;
            }

            _log.LogInformation("Alerta de seguridad enviada por webhook");
        }
        catch (Exception ex)
        {
            // La URL puede contener un token en el query string. No registrar la
            // excepción completa porque algunos handlers incluyen el URI solicitado.
            _log.LogError(
                "Fallo enviando alerta por webhook ({ExceptionType})",
                ex.GetType().Name);
        }
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
