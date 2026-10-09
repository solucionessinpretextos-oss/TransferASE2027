// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Vistas/VentanaArchivosLocales.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Code-behind WPF: conecta eventos visuales muy específicos con el ViewModel sin concentrar regla de negocio.
// Interacción: Inicializa componentes, asigna DataContext y maneja eventos de ventana/diálogos que XAML no expresa bien.
// Regla de mantenimiento: Evitar meter lógica de negocio aquí; si crece, moverla al ViewModel.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;


// ============================================================================
//  VentanaArchivosLocales.xaml.cs
// ============================================================================
//  Ventana de consulta/baja de documentos ya registrados. Se usa desde la pantalla
//  principal para revisar qué archivos quedaron anexados a un requerimiento o resultado.
// ============================================================================
namespace TransferClienteASE.Vistas
{
    public partial class VentanaArchivosLocales : Window
    {
        private readonly int _idRequerimiento;
        private readonly int _idColabora;
        private readonly int _idResultado;
        private readonly int _idMovimiento;
        private readonly bool _esResultado;

        private readonly string _rfcUsuario;
        private readonly ServicioArchivosLocales _servicioArchivos;
        private readonly ObservableCollection<ArchivoLocalDto> _archivos;

        private sealed class VmVentanaArchivos
        {
            public string NombreUsuario => SesionAplicacion.NombreUsuario ?? string.Empty;
            public string RfcUsuario => SesionAplicacion.Rfc ?? string.Empty;

            public string TituloArchivos { get; }
            public string MensajeEstado { get; }

            public VmVentanaArchivos(bool esResultado)
            {
                TituloArchivos = esResultado
                    ? "Archivos registrados para el resultado seleccionado"
                    : "Archivos registrados para el requerimiento seleccionado";

                MensajeEstado = esResultado
                    ? "Se muestran los archivos registrados para el resultado seleccionado."
                    : "Se muestran los archivos registrados para el requerimiento seleccionado.";
            }
        }

        public VentanaArchivosLocales(
            IEnumerable<ArchivoLocalDto> archivos,
            int idRequerimiento,
            int idColabora,
            string rfcUsuario)
            : this(archivos, idRequerimiento, idColabora, rfcUsuario, esResultado: false, idResultado: 0)
        {
        }

        public VentanaArchivosLocales(
            IEnumerable<ArchivoLocalDto> archivos,
            int idRequerimiento,
            int idColabora,
            string rfcUsuario,
            bool esResultado,
            int idResultado,
            int idMovimiento = 0)
        {
            InitializeComponent();

            _idRequerimiento = idRequerimiento;
            _idColabora = idColabora;
            _rfcUsuario = rfcUsuario?.Trim().ToUpperInvariant() ?? string.Empty;

            _esResultado = esResultado;
            _idResultado = idResultado;
            _idMovimiento = idMovimiento;

            var configuracion = new ServicioConfiguracionCliente();

            var handler = configuracion.CrearManejadorHttpSeguro();

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(configuracion.UrlBaseServidor, UriKind.Absolute)
            };

            _servicioArchivos = new ServicioArchivosLocales(
                httpClient,
                () => SesionAplicacion.TokenJwt);

            _archivos = new ObservableCollection<ArchivoLocalDto>(archivos ?? Array.Empty<ArchivoLocalDto>());
            DataGridArchivos.ItemsSource = _archivos;

            DataContext = new VmVentanaArchivos(_esResultado);

            Title = _esResultado ? "ASE | Archivos del Resultado" : "ASE | Archivos del Requerimiento";
        }

        private void BotonCerrar_Click(object sender, RoutedEventArgs e) => Close();

        private async void BotonEliminar_Click(object sender, RoutedEventArgs e)
        {
            var seleccionados = DataGridArchivos.SelectedItems
                .Cast<ArchivoLocalDto>()
                .ToList();

            if (seleccionados.Count == 0)
            {
                MessageBox.Show(
                    "Seleccione al menos un archivo de la lista para eliminar.",
                    "Eliminar archivos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var sufijo = _esResultado ? "resultado" : "requerimiento";

            var mensaje = seleccionados.Count == 1
                ? $"¿Está seguro que desea eliminar el archivo seleccionado del {sufijo}?\n\n" +
                  "Esta acción no elimina el archivo físico en su equipo, solo lo da de baja en el sistema."
                : $"¿Está seguro que desea eliminar los {seleccionados.Count} archivos seleccionados del {sufijo}?\n\n" +
                  "Esta acción no elimina los archivos físicos en su equipo, solo los da de baja en el sistema.";

            var dialogo = new DialogoImportanteWindow(
                "Confirmar eliminación de archivos",
                mensaje,
                TipoDialogo.Advertencia)
            {
                Owner = this
            };

            var confirmar = dialogo.ShowDialog();
            if (confirmar != true)
                return;

            try
            {
                var ids = seleccionados.Select(a => a.IdArchivoLocal).ToList();
                var ct = CancellationToken.None;

                int eliminados;

                if (_esResultado)
                {
                    eliminados = await _servicioArchivos.EliminarArchivosLocalesPorResultadoAsync(
                        _rfcUsuario,
                        _idResultado,
                        _idColabora,
                        _idMovimiento,
                        ids,
                        ct);
                }
                else
                {
                    eliminados = await _servicioArchivos.EliminarArchivosLocalesAsync(
                        _rfcUsuario,
                        _idRequerimiento,
                        _idColabora,
                        ids,
                        ct);
                }

                if (eliminados > 0)
                {
                    foreach (var item in seleccionados)
                        _archivos.Remove(item);

                    MessageBox.Show(
                        "Los archivos seleccionados se dieron de baja correctamente.",
                        "Eliminar archivos",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        "No se pudo dar de baja ningún archivo. Verifique e intente nuevamente.",
                        "Eliminar archivos",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error al eliminar archivos:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
