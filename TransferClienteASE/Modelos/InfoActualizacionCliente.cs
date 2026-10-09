// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/InfoActualizacionCliente.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace TransferClienteASE.Modelos
{
    public sealed class InfoActualizacionCliente
    {
        public string IdCliente { get; set; } = string.Empty;
        public string VersionMasReciente { get; set; } = string.Empty;
        public string VersionMinimaSoportada { get; set; } = string.Empty;
        public string UrlDescarga { get; set; } = string.Empty;
        public string? NotasVersion { get; set; }
        public bool EsActualizacionCritica { get; set; }
    }
}
