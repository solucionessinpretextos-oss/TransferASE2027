// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/Comando.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TransferClienteASE.Infraestructura
{
    public sealed class ComandoSimple : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public ComandoSimple(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
            => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter)
            => _execute(parameter);

        public event EventHandler? CanExecuteChanged;

        public void ElevarCanExecuteChanged()
            => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class ComandoAsync : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public ComandoAsync(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_isExecuting)
                return false;

            return _canExecute?.Invoke(parameter) ?? true;
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;

            try
            {
                _isExecuting = true;
                ElevarCanExecuteChanged();
                await _executeAsync(parameter);
            }
            finally
            {
                _isExecuting = false;
                ElevarCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void ElevarCanExecuteChanged()
            => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
