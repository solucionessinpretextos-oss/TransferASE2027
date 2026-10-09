// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/IServicioDialogos.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;

namespace TransferClienteASE.Infraestructura
{
    public enum TipoDialogo
    {
        Informacion,
        Advertencia,
        Error
    }

    public interface IServicioDialogos
    {
        void MostrarMensajeImportante(string titulo, string mensaje, TipoDialogo tipo = TipoDialogo.Informacion);

        bool MostrarConfirmacion(string titulo, string mensaje, TipoDialogo tipo = TipoDialogo.Advertencia);
    }
}
