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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
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
        private CancellationTokenSource? _descargaCts;

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

        protected override void OnClosed(EventArgs e)
        {
            _descargaCts?.Cancel();
            _descargaCts?.Dispose();
            _descargaCts = null;
            base.OnClosed(e);
        }

        private async void BotonDescargar_Click(object sender, RoutedEventArgs e)
        {
            var seleccionados = DataGridArchivos.SelectedItems
                .Cast<ArchivoLocalDto>()
                .ToList();

            if (seleccionados.Count == 0)
            {
                MessageBox.Show(
                    "Seleccione al menos un archivo para descargar.",
                    "Descargar archivos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var destinos = SeleccionarDestinos(seleccionados);
            if (destinos is null)
                return;

            _descargaCts?.Dispose();
            _descargaCts = new CancellationTokenSource();
            CambiarEstadoDescarga(true);

            try
            {
                for (var index = 0; index < destinos.Count; index++)
                {
                    var actual = destinos[index];
                    var fileIndex = index;
                    TextoArchivoDescarga.Text =
                        $"Archivo {index + 1}/{destinos.Count}: {actual.Archivo.NombreArchivo}";

                    var progreso = new Progress<DescargaArchivoProgreso>(estado =>
                    {
                        ProgresoArchivoDescarga.Value = estado.Porcentaje;
                        TextoPorcentajeArchivo.Text = $"{estado.Porcentaje:0.0} %";
                        var totalPercentage = ((fileIndex + estado.Porcentaje / 100d) / destinos.Count) * 100d;
                        ProgresoTotalDescarga.Value = totalPercentage;
                        TextoPorcentajeTotal.Text = $"{totalPercentage:0.0} %";
                        TextoAccionDescarga.Text = estado.Accion;

                        if (estado.BytesTotales.GetValueOrDefault() > 0)
                        {
                            TextoArchivoDescarga.Text =
                                $"Archivo {fileIndex + 1}/{destinos.Count}: {actual.Archivo.NombreArchivo} · " +
                                $"{FormatearBytes(estado.BytesRecibidos)} de {FormatearBytes(estado.BytesTotales!.Value)}";
                        }
                    });

                    await _servicioArchivos.DescargarArchivoAsync(
                        actual.Archivo.IdArchivoLocal,
                        actual.RutaDestino,
                        progreso,
                        _descargaCts.Token);
                }

                ProgresoArchivoDescarga.Value = 100;
                ProgresoTotalDescarga.Value = 100;
                TextoPorcentajeArchivo.Text = "100 %";
                TextoPorcentajeTotal.Text = "100 %";
                TextoAccionDescarga.Text = "Descargas verificadas y guardadas";

                MessageBox.Show(
                    seleccionados.Count == 1
                        ? "El archivo se descargó y verificó correctamente."
                        : $"Los {seleccionados.Count} archivos se descargaron y verificaron correctamente.",
                    "Descarga completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                TextoAccionDescarga.Text = "Descarga cancelada; el avance se conservará para continuar después";
            }
            catch (Exception ex)
            {
                TextoAccionDescarga.Text = "La descarga fue detenida de forma segura";
                MessageBox.Show(
                    ex.Message,
                    "No se pudo completar la descarga",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                CambiarEstadoDescarga(false);
                _descargaCts?.Dispose();
                _descargaCts = null;
            }
        }

        private void BotonCancelarDescarga_Click(object sender, RoutedEventArgs e)
        {
            TextoAccionDescarga.Text = "Cancelando descarga de forma segura...";
            _descargaCts?.Cancel();
        }

        private List<(ArchivoLocalDto Archivo, string RutaDestino)>? SeleccionarDestinos(
            IReadOnlyList<ArchivoLocalDto> archivos)
        {
            if (archivos.Count == 1)
            {
                var archivo = archivos[0];
                var dialog = new SaveFileDialog
                {
                    Title = "Guardar archivo verificado",
                    FileName = SanitizarNombreArchivo(archivo.NombreArchivo),
                    AddExtension = true,
                    OverwritePrompt = true,
                    Filter = "Todos los archivos (*.*)|*.*"
                };

                return dialog.ShowDialog(this) == true
                    ? new List<(ArchivoLocalDto, string)> { (archivo, dialog.FileName) }
                    : null;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = $"Seleccione la carpeta para guardar {archivos.Count} archivos",
                Multiselect = false
            };
            if (folderDialog.ShowDialog(this) != true)
                return null;

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return archivos
                .Select(archivo =>
                {
                    var destination = ObtenerRutaDisponible(
                        folderDialog.FolderName,
                        SanitizarNombreArchivo(archivo.NombreArchivo),
                        reserved);
                    reserved.Add(destination);
                    return (archivo, destination);
                })
                .ToList();
        }

        private void CambiarEstadoDescarga(bool descargando)
        {
            PanelProgresoDescarga.Visibility = Visibility.Visible;
            BotonDescargar.IsEnabled = !descargando;
            BotonEliminar.IsEnabled = !descargando;
            DataGridArchivos.IsEnabled = !descargando;
            BotonCancelarDescarga.Visibility = descargando
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (descargando)
            {
                ProgresoArchivoDescarga.Value = 0;
                ProgresoTotalDescarga.Value = 0;
                TextoPorcentajeArchivo.Text = "0 %";
                TextoPorcentajeTotal.Text = "0 %";
            }
        }

        private static string SanitizarNombreArchivo(string? name)
        {
            var safe = Path.GetFileName(name ?? string.Empty);
            foreach (var invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(safe) ? "archivo-descargado.bin" : safe;
        }

        private static string ObtenerRutaDisponible(
            string folder,
            string fileName,
            ISet<string> reserved)
        {
            var candidate = Path.Combine(folder, fileName);
            if (!File.Exists(candidate) && !reserved.Contains(candidate))
                return candidate;

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var suffix = 1; suffix < 10_000; suffix++)
            {
                candidate = Path.Combine(folder, $"{baseName} ({suffix}){extension}");
                if (!File.Exists(candidate) && !reserved.Contains(candidate))
                    return candidate;
            }

            throw new IOException("No se pudo crear un nombre disponible para la descarga.");
        }

        private static string FormatearBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var value = Math.Max(0, bytes);
            var unit = 0;
            double display = value;
            while (display >= 1024 && unit < units.Length - 1)
            {
                display /= 1024;
                unit++;
            }

            return $"{display:0.##} {units[unit]}";
        }

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
