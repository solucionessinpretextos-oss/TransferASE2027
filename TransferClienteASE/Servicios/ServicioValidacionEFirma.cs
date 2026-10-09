// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioValidacionEFirma.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio del cliente: encapsula HTTP REST, gRPC, e.firma, configuración, actualización o utilidades de negocio.
// Interacción: Los ViewModels llaman estos servicios; aquí se traduce la UI a llamadas hacia FileXfer.Server.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace TransferClienteASE.Servicios
{
    public sealed class ServicioValidacionEFirma
    {
        public record ResultadoValidacion(
            bool EsValido,
            string? RfcExtraido,
            string? MensajeError,
            X509Certificate2? Certificado);

        public ResultadoValidacion ValidarLocalmente(string rutaCer)
        {
            if (!File.Exists(rutaCer))
                return new ResultadoValidacion(false, null, "No se encontró el archivo CER.", null);

            try
            {
                var cert = new X509Certificate2(File.ReadAllBytes(rutaCer));

                if (DateTime.Now < cert.NotBefore)
                    return new ResultadoValidacion(false, null, "El certificado aún no es vigente.", cert);

                if (DateTime.Now > cert.NotAfter)
                    return new ResultadoValidacion(false, null, "El certificado ha expirado.", cert);

                var rfc = ExtraerRfc(cert);
                if (string.IsNullOrWhiteSpace(rfc))
                    return new ResultadoValidacion(false, null, "No se pudo extraer el RFC del certificado.", cert);

                return new ResultadoValidacion(true, rfc, null, cert);
            }
            catch (Exception ex)
            {
                return new ResultadoValidacion(false, null, $"Error al leer el certificado: {ex.Message}", null);
            }
        }

        private static string? ExtraerRfc(X509Certificate2 cert)
        {
            var subject = cert.Subject ?? string.Empty;

            const string prefijoOid = "OID.2.5.4.45=";
            var idx = subject.IndexOf(prefijoOid, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var resto = subject[(idx + prefijoOid.Length)..];
                var fin = resto.IndexOf(',');
                var valor = (fin >= 0 ? resto[..fin] : resto).Trim();
                if (!string.IsNullOrWhiteSpace(valor))
                    return valor.ToUpperInvariant();
            }

            const string prefijoSerial = "SERIALNUMBER=";
            idx = subject.IndexOf(prefijoSerial, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var resto = subject[(idx + prefijoSerial.Length)..];
                var fin = resto.IndexOf(',');
                var valor = (fin >= 0 ? resto[..fin] : resto).Trim();
                if (!string.IsNullOrWhiteSpace(valor))
                    return valor.ToUpperInvariant();
            }

            return null;
        }
    }
}
