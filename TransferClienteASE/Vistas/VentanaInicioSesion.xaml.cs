// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Vistas/VentanaInicioSesion.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Code-behind WPF: conecta eventos visuales muy específicos con el ViewModel sin concentrar regla de negocio.
// Interacción: Inicializa componentes, asigna DataContext y maneja eventos de ventana/diálogos que XAML no expresa bien.
// Regla de mantenimiento: Evitar meter lógica de negocio aquí; si crece, moverla al ViewModel.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Windows;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.ModelosVista;
using TransferClienteASE.Servicios;

namespace TransferClienteASE.Vistas
{
    public partial class VentanaInicioSesion : Window
    {
        private readonly ModeloVistaInicioSesion _modeloVista;

        public VentanaInicioSesion()
        {
            InitializeComponent();

            var servicioConfiguracion = new ServicioConfiguracionCliente();
            var servicioVersion = new ServicioVersion(servicioConfiguracion);
            var servicioAutenticacion = new ServicioAutenticacion(servicioConfiguracion);
            var servicioDialogos = new ServicioDialogosWpf();

            _modeloVista = new ModeloVistaInicioSesion(
                servicioConfiguracion,
                servicioVersion,
                servicioAutenticacion,
                servicioDialogos);

            DataContext = _modeloVista;

            _modeloVista.InicioSesionExitoso += ModeloVista_InicioSesionExitoso;

            Loaded += VentanaInicioSesion_Loaded;
        }

        private async void VentanaInicioSesion_Loaded(object sender, RoutedEventArgs e)
        {
            await _modeloVista.RevisarVersionAlInicioAsync();
        }

        private void ModeloVista_InicioSesionExitoso(object? sender, string rfc)
        {
            CajaContrasena.Clear();
            _modeloVista.Contrasena = string.Empty;

            var ventanaSelector = new VentanaSelectorRequerimiento(rfc)
            {
                Owner = this
            };

            this.Hide();

            var resultado = ventanaSelector.ShowDialog();
            this.Close();
        }

        private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is ModeloVistaInicioSesion vm &&
                sender is System.Windows.Controls.PasswordBox pb)
            {
                vm.Contrasena = pb.Password;
            }
        }
    }
}
