// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/ResultadoRevisionVersion.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;

namespace TransferClienteASE.Modelos
{
    public sealed class ResultadoRevisionVersion
    {
        public Version VersionLocal { get; init; } = new Version(0, 0, 0, 0);
        public Version VersionMasReciente { get; init; } = new Version(0, 0, 0, 0);
        public Version VersionMinimaSoportada { get; init; } = new Version(0, 0, 0, 0);
        public InfoActualizacionCliente? InfoActualizacion { get; init; }
        public bool HayActualizacionDisponible { get; init; }
        public bool ActualizacionObligatoria { get; init; }
        public string? MensajeError { get; init; }
    }
}
