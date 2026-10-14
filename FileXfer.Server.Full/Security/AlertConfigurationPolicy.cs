using System.Net;
using System.Net.Mail;
using FileXfer.Server.Options;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Security;

public static class AlertConfigurationPolicy
{
    private const int MaxRecipients = 25;

    public static IReadOnlyList<string> GetErrors(AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        if (!options.Enabled)
            return errors;

        var hasEmail = HasAnyValue(
            options.EmailTo,
            options.EmailFrom,
            options.SmtpHost,
            options.SmtpUser,
            options.SmtpPassword);
        var hasWebhook = !string.IsNullOrWhiteSpace(options.WebhookUrl);

        if (!hasEmail && !hasWebhook)
        {
            errors.Add(
                "Alerts:Enabled=true requiere configurar un canal externo completo (email o webhook HTTPS).");
        }

        if (hasEmail)
            ValidateEmail(options, errors);

        if (hasWebhook)
            ValidateWebhook(options, errors);

        if (options.SmtpTimeoutSeconds is < 1 or > 120)
            errors.Add("Alerts:SmtpTimeoutSeconds debe estar entre 1 y 120.");

        if (options.WebhookTimeoutSeconds is < 1 or > 120)
            errors.Add("Alerts:WebhookTimeoutSeconds debe estar entre 1 y 120.");

        return errors;
    }

    private static void ValidateEmail(AlertOptions options, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost))
        {
            errors.Add("Alerts:SmtpHost es obligatorio cuando se configura email.");
        }
        else
        {
            var host = options.SmtpHost.Trim();
            if (ContainsControlCharacters(host) ||
                (Uri.CheckHostName(host) == UriHostNameType.Unknown && !IPAddress.TryParse(host, out _)))
            {
                errors.Add("Alerts:SmtpHost debe ser únicamente un nombre DNS o una dirección IP válida.");
            }
        }

        if (options.SmtpPort is < 1 or > 65_535)
            errors.Add("Alerts:SmtpPort debe estar entre 1 y 65535.");

        if (!options.SmtpUseTls)
            errors.Add("Alerts:SmtpUseTls debe ser true; SMTP sin TLS no está permitido.");

        ValidateAddress("Alerts:EmailFrom", options.EmailFrom, errors);

        if (string.IsNullOrWhiteSpace(options.EmailTo))
        {
            errors.Add("Alerts:EmailTo es obligatorio cuando se configura email.");
        }
        else
        {
            var recipients = options.EmailTo.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (recipients.Length == 0)
                errors.Add("Alerts:EmailTo debe contener al menos un destinatario.");
            if (recipients.Length > MaxRecipients)
                errors.Add($"Alerts:EmailTo no puede contener más de {MaxRecipients} destinatarios.");

            foreach (var recipient in recipients)
                ValidateAddress("Alerts:EmailTo", recipient, errors);
        }

        var hasUser = !string.IsNullOrWhiteSpace(options.SmtpUser);
        var hasPassword = !string.IsNullOrEmpty(options.SmtpPassword);
        if (hasUser != hasPassword)
        {
            errors.Add(
                "Alerts:SmtpUser y Alerts:SmtpPassword deben proporcionarse juntos o dejarse ambos vacíos.");
        }

        if (hasUser && ContainsControlCharacters(options.SmtpUser!))
            errors.Add("Alerts:SmtpUser contiene caracteres de control no permitidos.");
    }

    private static void ValidateWebhook(AlertOptions options, ICollection<string> errors)
    {
        if (!Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add("Alerts:WebhookUrl debe ser una URL HTTPS absoluta.");
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            errors.Add("Alerts:WebhookUrl no puede incluir credenciales en la URL.");

        if (!string.IsNullOrEmpty(uri.Fragment))
            errors.Add("Alerts:WebhookUrl no puede incluir fragmentos (#...).");
    }

    private static void ValidateAddress(
        string settingName,
        string? value,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            ContainsControlCharacters(value) ||
            !MailAddress.TryCreate(value, out var parsed) ||
            !string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{settingName} debe contener una dirección de correo simple y válida.");
        }
    }

    private static bool HasAnyValue(params string?[] values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static bool ContainsControlCharacters(string value) =>
        value.Any(char.IsControl);
}

public sealed class AlertOptionsValidator : IValidateOptions<AlertOptions>
{
    public ValidateOptionsResult Validate(string? name, AlertOptions options)
    {
        var errors = AlertConfigurationPolicy.GetErrors(options);
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
