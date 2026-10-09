// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/NotificacionPropiedadBase.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TransferClienteASE.Infraestructura
{
    public abstract class NotificacionPropiedadBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void NotificarCambio([CallerMemberName] string? nombrePropiedad = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nombrePropiedad));
        }

        protected bool EstablecerValor<T>(ref T campo, T valor, [CallerMemberName] string? nombrePropiedad = null)
        {
            if (EqualityComparer<T>.Default.Equals(campo, valor))
                return false;

            campo = valor;
            NotificarCambio(nombrePropiedad);
            return true;
        }
    }
}
