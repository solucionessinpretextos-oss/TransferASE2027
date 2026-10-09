// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/ComandoRelay.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Windows.Input;

namespace TransferClienteASE.Infraestructura
{
    public sealed class ComandoRelay : ICommand
    {
        private readonly Action<object?> _ejecutar;
        private readonly Func<object?, bool>? _puedeEjecutar;

        public ComandoRelay(Action<object?> ejecutar, Func<object?, bool>? puedeEjecutar = null)
        {
            _ejecutar = ejecutar ?? throw new ArgumentNullException(nameof(ejecutar));
            _puedeEjecutar = puedeEjecutar;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            _puedeEjecutar?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) =>
            _ejecutar(parameter);

        public void ElevarCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
