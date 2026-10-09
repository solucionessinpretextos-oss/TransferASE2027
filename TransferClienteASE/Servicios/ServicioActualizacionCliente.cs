// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioActualizacionCliente.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio de actualización del cliente.
// Interacción: Consulta versiones y descarga paquetes para el updater.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransferClienteASE.Servicios
{
    public sealed class VersionInfo
    {
        public string ClienteId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string UrlDescarga { get; set; } = string.Empty;
        public string Notas { get; set; } = string.Empty;
        public bool Obligatoria { get; set; }

        public Version? ParseVersion() =>
            System.Version.TryParse(Version, out var v) ? v : null;
    }

    public sealed class ServicioActualizacionCliente
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;

        public ServicioActualizacionCliente(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        }

        public async Task<VersionInfo?> ObtenerUltimaVersionAsync(
            string clienteId,
            CancellationToken ct = default)
        {
            var versionLocal = ObtenerVersionLocal();
            var url = $"actualizaciones/transfer-cliente?clienteId={clienteId}&versionLocal={versionLocal}";

            var rsp = await _http.GetAsync(url, ct);

            if (rsp.StatusCode == HttpStatusCode.NoContent)
                return null;

            if (!rsp.IsSuccessStatusCode)
                return null; // silencioso: no rompemos nada

            var info = await rsp.Content.ReadFromJsonAsync<VersionInfo>(_json, ct);
            return info;
        }

        public static string ObtenerVersionLocal()
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v?.ToString() ?? "0.0.0.0";
        }
    }
}
