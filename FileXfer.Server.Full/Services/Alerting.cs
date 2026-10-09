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
    public CompositeAlertSink(IEnumerable<IAlertSink> sinks) { _sinks = sinks; }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        foreach (var s in _sinks) { try { await s.NotifySecurityEventAsync(title, message, ct); } catch {  } }
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
                EnableSsl = _opt.SmtpUseTls
            };
            if (!string.IsNullOrWhiteSpace(_opt.SmtpUser))
                smtp.Credentials = new NetworkCredential(_opt.SmtpUser, _opt.SmtpPassword);

            var mail = new MailMessage
            {
                From = new MailAddress(_opt.EmailFrom!),
                Subject = $"[ALERTA] {title}",
                Body = message,
                BodyEncoding = Encoding.UTF8
            };

            foreach (var to in _opt.EmailTo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mail.To.Add(to);

            await smtp.SendMailAsync(mail);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fallo enviando alerta por email");
        }
    }
}

public sealed class WebhookAlertSink : IAlertSink
{
    private readonly AlertOptions _opt;
    private readonly ILogger<WebhookAlertSink> _log;
    private readonly HttpClient _http = new();

    public WebhookAlertSink(IOptions<AlertOptions> opt, ILogger<WebhookAlertSink> log)
    {
        _opt = opt.Value; _log = log;
    }

    public async Task NotifySecurityEventAsync(string title, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.WebhookUrl)) return;
        try
        {
            var payload = new { text = $"[ALERTA] {title}\n{message}" };
            using var resp = await _http.PostAsJsonAsync(_opt.WebhookUrl!, payload, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fallo enviando alerta por webhook");
        }
    }
}
