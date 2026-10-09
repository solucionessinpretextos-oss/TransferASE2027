// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Vistas/VentanaPrincipal.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Code-behind WPF: conecta eventos visuales muy específicos con el ViewModel sin concentrar regla de negocio.
// Interacción: Inicializa componentes, asigna DataContext y maneja eventos de ventana/diálogos que XAML no expresa bien.
// Regla de mantenimiento: Evitar meter lógica de negocio aquí; si crece, moverla al ViewModel.
// Notas específicas:
// - Propiedades calculadas o solo lectura deben enlazarse con Mode=OneWay para evitar InvalidOperationException de WPF.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Windows;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.ModelosVista;
using TransferClienteASE.Servicios;


// ============================================================================
//  VentanaPrincipal.xaml.cs
// ============================================================================
//  Código behind de la ventana de carga. Mantiene interacción visual con WPF y delega
//  la lógica de carga a ModeloVistaPrincipal.
// ============================================================================
namespace TransferClienteASE.Vistas
{
    public partial class VentanaPrincipal : Window
    {
        public bool SeSolicitoSalir { get; private set; }

        public VentanaPrincipal(ContextoRequerimiento contexto)
        {
            InitializeComponent();

            var configuracion = new ServicioConfiguracionCliente();
            var servicioCarga = new ServicioCargaArchivos(configuracion);

            var modeloVista = new ModeloVistaPrincipal(servicioCarga, contexto);
            DataContext = modeloVista;
        }

        private void BotonSalir_Click(object sender, RoutedEventArgs e)
        {
            var dialogo = new DialogoImportanteWindow(
                "Confirmar salida",
                "Se cerrará esta ventana y regresará a la selección de requerimientos.\n\n" +
                "Si tiene archivos pendientes de subir, estos no serán enviados.",
                TipoDialogo.Advertencia)
            {
                Owner = this
            };

            if (dialogo.ShowDialog() == true)
            {
                SeSolicitoSalir = true;
                Close();
            }
        }

        private void BotonConfirmarSubir_Click(object sender, RoutedEventArgs e)
        {
            var dialogo = new DialogoImportanteWindow(
                "Confirmar subida",
                "Est usted seguro de subir los archivos seleccionados?",
                TipoDialogo.Advertencia)
            {
                Owner = this
            };

            if (dialogo.ShowDialog() == true && DataContext is ModeloVistaPrincipal vm)
            {
                if (vm.ComandoSubirArchivos.CanExecute(null))
                    vm.ComandoSubirArchivos.Execute(null);
            }
        }

        private void BotonCancelarCola_Click(object sender, RoutedEventArgs e)
        {
            var dialogo = new DialogoImportanteWindow(
                "Cancelar cola",
                "Est seguro de cancelar la subida de archivos?\n\n" +
                "Los archivos en proceso o pendientes no sern enviados.",
                TipoDialogo.Advertencia)
            {
                Owner = this
            };

            if (dialogo.ShowDialog() == true && DataContext is ModeloVistaPrincipal vm)
            {
                if (vm.ComandoPausarCola.CanExecute(null))
                    vm.ComandoPausarCola.Execute(null);

                vm.MensajeEstado = "La cola de archivos fue cancelada.";
            }
        }
    }
}
