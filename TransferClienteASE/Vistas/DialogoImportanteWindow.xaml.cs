// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Vistas/DialogoImportanteWindow.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Code-behind WPF: conecta eventos visuales muy específicos con el ViewModel sin concentrar regla de negocio.
// Interacción: Inicializa componentes, asigna DataContext y maneja eventos de ventana/diálogos que XAML no expresa bien.
// Regla de mantenimiento: Evitar meter lógica de negocio aquí; si crece, moverla al ViewModel.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Windows;
using System.Windows.Media;
using TransferClienteASE.Infraestructura;

namespace TransferClienteASE.Vistas
{
    public partial class DialogoImportanteWindow : Window
    {
        public DialogoImportanteWindow(string titulo, string mensaje, TipoDialogo tipo)
        {
            InitializeComponent();

            TextoTitulo.Text = titulo ?? string.Empty;
            TextoMensaje.Text = mensaje ?? string.Empty;

            switch (tipo)
            {
                case TipoDialogo.Error:
                    TextoTitulo.Foreground = Brushes.DarkRed;
                    break;
                case TipoDialogo.Advertencia:
                    TextoTitulo.Foreground = Brushes.DarkGoldenrod;
                    break;
                default:
                    TextoTitulo.Foreground = Brushes.DarkSlateGray;
                    break;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AplicarLimitesDePantalla();
        }

        private void AplicarLimitesDePantalla()
        {
            var work = SystemParameters.WorkArea;

            MaxHeight = work.Height * 0.90;

            var maxW = work.Width * 0.90;
            MaxWidth = maxW;

            if (Width > MaxWidth)
                Width = MaxWidth;

            InvalidateVisual();
            UpdateLayout();
        }

        private void BotonAceptar_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
