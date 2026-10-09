// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Vistas/VentanaSelectorRequerimiento.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Code-behind WPF: conecta eventos visuales muy específicos con el ViewModel sin concentrar regla de negocio.
// Interacción: Inicializa componentes, asigna DataContext y maneja eventos de ventana/diálogos que XAML no expresa bien.
// Regla de mantenimiento: Evitar meter lógica de negocio aquí; si crece, moverla al ViewModel.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Threading;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.ModelosVista;
using TransferClienteASE.Servicios;


// ============================================================================
//  VentanaSelectorRequerimiento.xaml.cs
// ============================================================================
//  Código behind mínimo de la pantalla de selección. Su objetivo es conectar eventos
//  de la ventana con el ViewModel. La lógica de negocio debe vivir en
//  ModeloVistaSelectorRequerimiento, no aquí.
// ============================================================================
namespace TransferClienteASE.Vistas
{
    public partial class VentanaSelectorRequerimiento : Window
    {
        private readonly ModeloVistaSelectorRequerimiento _modeloVista;

        public ContextoRequerimiento? ContextoSeleccionado { get; private set; }

        private const double AlturaFila = 32.0;

        private const double MargenExtraTabla = 48.0;

        public VentanaSelectorRequerimiento(string rfcUsuario)
        {
            InitializeComponent();

            var configuracion = new ServicioConfiguracionCliente();

            var handler = configuracion.CrearManejadorHttpSeguro();

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuracion.UrlBaseServidor, UriKind.Absolute)
            };

            var servicioCatalogos = new ServicioCatalogosAuditoria(
                httpClient,
                configuracion.UrlBaseServidor,
                () => SesionAplicacion.TokenJwt);

            _modeloVista = new ModeloVistaSelectorRequerimiento(servicioCatalogos, rfcUsuario);
            _modeloVista.RequerimientoConfirmado += ModeloVista_RequerimientoConfirmado;

            DataContext = _modeloVista;
        }

        private async void ModeloVista_RequerimientoConfirmado(object? sender, ContextoRequerimiento e)
        {
            ContextoSeleccionado = e;

            var ventanaPrincipal = new VentanaPrincipal(e)
            {
                Owner = this
            };

            ventanaPrincipal.ShowDialog();

            await _modeloVista.RefrescarRequerimientoDespuesDeSubirAsync();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.InvokeAsync(
                AjustarTamanoPaginaSegunVentana,
                DispatcherPriority.Background);

            // La comprobación segura y firmada se ejecuta en la pantalla de inicio de sesión.
            // El flujo legado de ZIP sin firma queda deliberadamente deshabilitado.
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 5)
            {
                Dispatcher.InvokeAsync(
                    AjustarTamanoPaginaSegunVentana,
                    DispatcherPriority.Background);
            }
        }

        private void AjustarTamanoPaginaSegunVentana()
        {
            var grid = (dgRequerimientos != null && dgRequerimientos.Visibility == Visibility.Visible)
                ? dgRequerimientos
                : (dgResultadosInforme != null && dgResultadosInforme.Visibility == Visibility.Visible)
                    ? dgResultadosInforme
                    : dgResultadosCedula;

            if (grid == null || grid.ActualHeight <= 0)
                return;

            var alturaUtil = grid.ActualHeight - MargenExtraTabla;
            if (alturaUtil <= AlturaFila)
            {
                _ = _modeloVista.ActualizarTamanoPaginaAsync(1);
                return;
            }

            var filasQueCaben = (int)(alturaUtil / AlturaFila);
            if (filasQueCaben < 1)
                filasQueCaben = 1;

            _ = _modeloVista.ActualizarTamanoPaginaAsync(filasQueCaben);
        }

        private void BotonAgregarDocumentos_Click(object sender, RoutedEventArgs e)
        {
            if (_modeloVista.ComandoContinuar.CanExecute(null))
            {
                _modeloVista.ComandoContinuar.Execute(null);
            }
        }

        private void BotonSalir_Click(object sender, RoutedEventArgs e)
        {
            var dialogo = new DialogoImportanteWindow(
                "Salir del sistema",
                "¿Está seguro que desea salir de la aplicación Transfer ASE?",
                TipoDialogo.Advertencia)
            {
                Owner = this
            };

            var resultado = dialogo.ShowDialog();
            if (resultado == true)
            {
                Application.Current.Shutdown();
            }
        }

        private void ComboNoEditable_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = true;
        }

        private void ComboNoEditable_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Home:
                case Key.End:
                case Key.Tab:
                case Key.Enter:
                case Key.Escape:
                case Key.F4:
                    return;

                default:
                    break;
            }

            e.Handled = true;
        }

        private async void VerificarActualizacionEnSegundoPlano()
        {
            try
            {
                var configuracion = new ServicioConfiguracionCliente();

                var handler = configuracion.CrearManejadorHttpSeguro();

                using var http = new HttpClient(handler)
                {
                    BaseAddress = new Uri(configuracion.UrlBaseServidor, UriKind.Absolute)
                };

                var servicioActualizacion = new ServicioActualizacionCliente(http);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var info = await servicioActualizacion.ObtenerUltimaVersionAsync(
                    "TransferClienteASE",
                    cts.Token);

                if (info is null)
                    return;

                var versionLocal = ServicioActualizacionCliente.ObtenerVersionLocal();
                var vLocal = Version.TryParse(versionLocal, out var vl) ? vl : new Version(0, 0, 0, 0);
                var vRemota = info.ParseVersion() ?? vLocal;

                if (vRemota <= vLocal)
                    return;

                var dialogo = new VentanaActualizacionDisponible(info, versionLocal)
                {
                    Owner = this
                };

                dialogo.ShowDialog();
            }
            catch
            {
            }
        }
    }
}
