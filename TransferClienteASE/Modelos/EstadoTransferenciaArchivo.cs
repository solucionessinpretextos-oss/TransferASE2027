// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/EstadoTransferenciaArchivo.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace TransferClienteASE.Modelos
{
    public enum EstadoTransferenciaArchivo
    {
        Pendiente = 0,
        EnCola = 1,
        Subiendo = 2,
        Completado = 3,
        Fallido = 4,
        Cancelado = 5,
        Rechazado = 6
    }
}
