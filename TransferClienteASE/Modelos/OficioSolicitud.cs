// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/OficioSolicitud.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;

namespace TransferClienteASE.Modelos
{
    public sealed class OficioSolicitud
    {
        public int Id { get; set; }
        public string Folio { get; set; } = string.Empty;
        public DateTime FechaEmision { get; set; }
        public override string ToString() => $"{Folio} ({FechaEmision:dd/MM/yyyy})";
    }
}
