// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/ServicioDialogosWpf.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Windows;
using TransferClienteASE.Vistas;

namespace TransferClienteASE.Infraestructura
{
    public sealed class ServicioDialogosWpf : IServicioDialogos
    {
        public void MostrarMensajeImportante(string titulo, string mensaje, TipoDialogo tipo = TipoDialogo.Informacion)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var ventana = new DialogoImportanteWindow(titulo, mensaje, tipo);

                if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
                {
                    ventana.Owner = Application.Current.MainWindow;
                    ventana.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    ventana.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                ventana.ShowDialog();
            });
        }

        public bool MostrarConfirmacion(string titulo, string mensaje, TipoDialogo tipo = TipoDialogo.Advertencia)
        {
            bool resultado = false;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var owner = Application.Current?.MainWindow;
                var buttons = MessageBoxButton.YesNo;
                var icon = MessageBoxImage.Question;

                switch (tipo)
                {
                    case TipoDialogo.Error:
                        icon = MessageBoxImage.Error;
                        break;
                    case TipoDialogo.Advertencia:
                        icon = MessageBoxImage.Warning;
                        break;
                    default:
                        icon = MessageBoxImage.Information;
                        break;
                }

                var respuesta = owner != null
                    ? MessageBox.Show(owner, mensaje, titulo, buttons, icon)
                    : MessageBox.Show(mensaje, titulo, buttons, icon);

                resultado = respuesta == MessageBoxResult.Yes;
            });

            return resultado;
        }
    }
}
