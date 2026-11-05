using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;

namespace FileXfer.Server.Services;

public static class AlertFailureDescription
{
    // Solo códigos seguros. Los mensajes de excepciones pueden contener URL o credenciales.
    public static string Code(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket) return "socket_" + socket.SocketErrorCode;
            if (current is AuthenticationException) return "tls_validation_failed";
        }
        return error switch
        {
            MailKit.Net.Smtp.SmtpCommandException smtp => "smtp_" + (int)smtp.StatusCode,
            MailKit.Security.AuthenticationException => "smtp_authentication_failed",
            MailKit.Security.SslHandshakeException => "tls_validation_failed",
            SmtpException smtp => "smtp_" + (int)smtp.StatusCode,
            OperationCanceledException => "delivery_timeout",
            _ => error.GetType().Name
        };
    }

    public static (string Explanation, string Remediation) Describe(string? code) => code switch
    {
        "socket_HostNotFound" or "socket_NoData" => ("No se pudo resolver el nombre del servidor de notificaciones.", "Corrija el host configurado y compruebe DNS desde el servidor FileXfer."),
        "socket_ConnectionRefused" => ("El servidor de notificaciones rechazó la conexión.", "Compruebe host, puerto, servicio SMTP y reglas de red."),
        "socket_TimedOut" or "delivery_timeout" or "TaskCanceledException" => ("Se agotó el tiempo de conexión o entrega.", "Compruebe conectividad y disponibilidad del destino antes de reintentar."),
        "tls_validation_failed" => ("No pudo establecerse una conexión TLS validada.", "Revise vigencia, nombre y confianza del certificado del destino. Mantenga la validación TLS habilitada."),
        "smtp_authentication_failed" or "smtp_530" or "smtp_535" or "http_401" or "http_403" => ("El destino rechazó la autenticación o los permisos.", "Revise las credenciales y los permisos del remitente o webhook mediante el almacén de secretos."),
        "smtp_550" or "smtp_553" => ("SMTP rechazó el remitente o destinatario.", "Verifique las direcciones y los permisos de envío o relay."),
        "transport_not_configured" => ("El canal de esta entrega no está configurado.", "Configure el canal correspondiente antes de volver a enviar esta alerta."),
        "SmtpException" or "smtp_-1" => ("La entrega SMTP falló. El registro histórico no contiene una causa más específica.", "Revise host, puerto, DNS, TLS y permisos SMTP. Los fallos nuevos incluyen un código más preciso cuando está disponible."),
        null or "" => ("No hay un fallo de entrega registrado.", "Revise el estado y la fecha del próximo intento."),
        _ => ("El transporte no pudo confirmar la entrega de la notificación.", "Revise el código de error y la configuración del canal. Una falla de notificación no implica por sí sola pérdida del archivo.")
    };
}
