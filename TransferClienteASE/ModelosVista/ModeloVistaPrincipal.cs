// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/ModelosVista/ModeloVistaPrincipal.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: ViewModel de carga de documentos.
// Interacción: Permite agregar archivos/carpetas, invoca gRPC, actualiza progreso y refresca documentos anexados.
// Regla de mantenimiento: Respetar INotifyPropertyChanged y no bloquear UI thread; errores aquí se manifiestan como pantallas congeladas o bindings rotos.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Grpc.Core; // <<< agregado para detectar cancelación gRPC
using FileXfer.Pdf;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;


// ============================================================================
//  ModeloVistaPrincipal
// ============================================================================
//  Controla la ventana donde el usuario agrega archivos, ve progreso y confirma la carga.
//  Recibe un ContextoRequerimiento ya resuelto por ModeloVistaSelectorRequerimiento.
//
//  Esta clase no decide de dónde salen los resultados; solo usa el contexto para saber
//  contra qué requerimiento o resultado debe subir documentos.
// ============================================================================
namespace TransferClienteASE.ModelosVista
{
    public sealed class ModeloVistaPrincipal : NotificacionPropiedadBase
    {
        private readonly ServicioCargaArchivos _servicioCarga;
        private readonly ContextoRequerimiento _contexto;

        private bool _subidaEnProgreso;
        private string _mensajeEstado = string.Empty;
        private CancellationTokenSource? _ctsSubida;

        private string _nombreUsuario = string.Empty;
        public string NombreUsuario
        {
            get => _nombreUsuario;
            set => EstablecerValor(ref _nombreUsuario, value);
        }

        private string _rfcUsuario = string.Empty;
        public string RfcUsuario
        {
            get => _rfcUsuario;
            set => EstablecerValor(ref _rfcUsuario, value);
        }

        private static string ObtenerStringDeSesion(params string[] posiblesNombresPropiedad)
        {
            try
            {
                var t = typeof(SesionAplicacion);

                foreach (var nombre in posiblesNombresPropiedad)
                {
                    var p = t.GetProperty(nombre);
                    if (p == null) continue;

                    var v = p.GetValue(null);
                    if (v == null) continue;

                    var s = v as string ?? v.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private void CargarUsuarioDesdeSesion()
        {
        }

        private static readonly string[] ExtensionesPermitidas =
        {
            // Los contenedores de llave privada (.key/.pfx/.p12/.der) y archivos .env
            // se excluyen deliberadamente para evitar exfiltración accidental de credenciales.
            ".pdf", ".xml", ".doc", ".docx", ".xls", ".xsd", ".xlsx", ".xlsm", ".xlt", ".xltx", ".xltm", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".txt", ".odt", ".rtf", ".md", ".wps", ".xps", ".oxps", ".epub", ".csv", ".tsv", ".tab", ".psv", ".ppt", ".pptx", ".odp", ".ods", ".ots", ".numbers", ".svg", ".svgz", ".ai", ".eps", ".cdr", ".dxf", ".dwg", ".vsd", ".vsdx", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".sql", ".sqlite", ".db", ".sqlite3", ".mdb", ".bak", ".accdb", ".gpg", ".asc", ".csr", ".eml", ".msg", ".mbox", ".pst", ".ost", ".ics", ".vcf", ".sua", ".cer"
        };

        public ContextoRequerimiento Contexto => _contexto;

        public bool EsSubidaParaResultado => _contexto?.ResultadoId > 0;

        public string TituloSubida =>
            EsSubidaParaResultado
                ? "Agregar documentos a resultado seleccionado"
                : "Agregar documentos a requerimiento seleccionado";

        public string EtiquetaElementoSeleccionado =>
            EsSubidaParaResultado
                ? "Resultado seleccionado"
                : "Requerimiento seleccionado";

        public ObservableCollection<ModeloVistaArchivoTransferencia> Archivos { get; } = new();

        public bool SubidaEnProgreso
        {
            get => _subidaEnProgreso;
            set
            {
                if (EstablecerValor(ref _subidaEnProgreso, value))
                {
                    ActualizarHabilitacionComandos();
                }
            }
        }

        public string MensajeEstado
        {
            get => _mensajeEstado;
            set => EstablecerValor(ref _mensajeEstado, value);
        }

        public ComandoRelay ComandoAgregarArchivos { get; }
        public ComandoRelay ComandoAgregarCarpetas { get; }
        public ComandoRelay ComandoQuitarArchivo { get; }
        public ComandoRelay ComandoSubirArchivos { get; }
        public ComandoRelay ComandoVerDocumentos { get; }
        public ComandoRelay ComandoPausarCola { get; }
        public ComandoRelay ComandoOrdenarPorNombre { get; }
        public ComandoRelay ComandoOrdenarPorTamanio { get; }

        public ModeloVistaPrincipal(ServicioCargaArchivos servicioCarga, ContextoRequerimiento contexto)
        {
            _servicioCarga = servicioCarga ?? throw new ArgumentNullException(nameof(servicioCarga));
            _contexto = contexto ?? throw new ArgumentNullException(nameof(contexto));

            NombreUsuario = ObtenerStringDeSesion("NombreUsuario", "Nombre", "Usuario", "NombreCompleto", "NombrePersona");
            RfcUsuario = ObtenerStringDeSesion("RfcUsuario", "Rfc", "RFC");

            ComandoAgregarArchivos = new ComandoRelay(_ => AgregarArchivos(), _ => !SubidaEnProgreso);
            ComandoAgregarCarpetas = new ComandoRelay(_ => AgregarCarpetas(), _ => !SubidaEnProgreso);
            ComandoQuitarArchivo = new ComandoRelay(a => QuitarArchivo(a as ModeloVistaArchivoTransferencia),
                _ => !SubidaEnProgreso && Archivos.Any());
            ComandoSubirArchivos = new ComandoRelay(async _ => await SubirArchivosAsync(),
                _ => !SubidaEnProgreso && Archivos.Any());
            ComandoVerDocumentos = new ComandoRelay(async _ => await VerDocumentosAsync(),
                _ => !SubidaEnProgreso);
            ComandoPausarCola = new ComandoRelay(_ => PausarCola(), _ => SubidaEnProgreso);
            ComandoOrdenarPorNombre = new ComandoRelay(_ => OrdenarColaPorNombre(),
                _ => !SubidaEnProgreso && Archivos.Any());
            ComandoOrdenarPorTamanio = new ComandoRelay(_ => OrdenarColaPorTamanio(),
                _ => !SubidaEnProgreso && Archivos.Any());

            Archivos.CollectionChanged += (_, __) =>
            {
                MensajeEstado = $"Archivos en cola: {Archivos.Count}";
                ActualizarHabilitacionComandos();
            };
        }

        private void AgregarArchivos()
        {
            var filtroDialogo =
                "Todos los archivos (*.*)|*.*|" +
                "PDF (*.pdf)|*.pdf|" +
                "XML (*.xml)|*.xml|" +
                "Word (*.doc;*.docx)|*.doc;*.docx|" +
                "Excel (*.xls;*.xlsx)|*.xls;*.xlsx|" +
                "TXT (*.txt)|*.txt";

            var dlg = new OpenFileDialog
            {
                Filter = filtroDialogo,
                Multiselect = true
            };

            var result = dlg.ShowDialog();
            if (result != true)
                return;

            AgregarRutasACola(dlg.FileNames);
        }

        private void AgregarCarpetas()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Seleccione la carpeta que desea agregar (se incluirán subcarpetas).",
                ShowNewFolderButton = false
            };

            var res = dlg.ShowDialog();
            if (res != System.Windows.Forms.DialogResult.OK)
                return;

            var ruta = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(ruta))
                return;

            IEnumerable<string> archivos;
            try
            {
                archivos = Directory.EnumerateFiles(ruta, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                var mensaje = ManejadorErroresCliente.ObtenerMensajeUsuario(
                    ex,
                    "leer el contenido de la carpeta seleccionada");

                MensajeEstado = mensaje;
                MessageBox.Show(
                    mensaje,
                    "Error al leer carpeta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var filtrados = archivos.Where(r =>
                ExtensionesPermitidas.Contains(Path.GetExtension(r) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase));

            AgregarRutasACola(filtrados);
        }

        private void AgregarRutasACola(IEnumerable<string> rutas)
        {
            foreach (var ruta in rutas)
            {
                if (!File.Exists(ruta))
                    continue;

                if (!ExtensionesPermitidas.Contains(Path.GetExtension(ruta) ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!Archivos.Any(a =>
                        string.Equals(a.RutaCompleta, ruta, StringComparison.OrdinalIgnoreCase)))
                {
                    Archivos.Add(new ModeloVistaArchivoTransferencia(ruta));
                }
            }
        }

        private void QuitarArchivo(ModeloVistaArchivoTransferencia? archivo)
        {
            if (archivo == null)
                return;

            Archivos.Remove(archivo);
        }

        /// <summary>
        /// Confirma la carga de todos los archivos seleccionados.
        ///
        /// El ViewModel delega la transferencia real a ServicioCargaArchivos. Al terminar,
        /// refresca estados visuales para que el usuario vea progreso 100% o error por archivo.
        /// </summary>
        private async Task SubirArchivosAsync()
        {
            if (!Archivos.Any())
                return;

            _ctsSubida = new CancellationTokenSource();
            var ct = _ctsSubida.Token;

            SubidaEnProgreso = true;
            MensajeEstado = "Iniciando carga de archivos...";

            try
            {
                var rutas = Archivos
                    .Where(a => a.Estado != EstadoTransferenciaArchivo.Completado)
                    .Select(a => a.RutaCompleta)
                    .ToArray();

                if (rutas.Length == 0)
                {
                    MensajeEstado = "No hay archivos pendientes por subir.";
                    SubidaEnProgreso = false;
                    return;
                }

                foreach (var a in Archivos)
                {
                    if (a.Estado == EstadoTransferenciaArchivo.Pendiente ||
                        a.Estado == EstadoTransferenciaArchivo.Fallido ||
                        a.Estado == EstadoTransferenciaArchivo.Cancelado)
                    {
                        a.Estado = EstadoTransferenciaArchivo.EnCola;
                        a.Progreso = 0;
                    }
                }

                var progreso = new Progress<ProgresoCargaArchivo>(p =>
                {
                    var modelo = Archivos.FirstOrDefault(a =>
                        string.Equals(a.RutaCompleta, p.RutaOriginal, StringComparison.OrdinalIgnoreCase));

                    if (modelo != null)
                    {
                        modelo.Progreso = p.Porcentaje;
                        modelo.Estado = p.Porcentaje >= 100.0
                            ? EstadoTransferenciaArchivo.Completado
                            : EstadoTransferenciaArchivo.Subiendo;
                    }

                    MensajeEstado = p.Mensaje;
                });

                var resultado = await _servicioCarga.EnviarArchivosAsync(Contexto, rutas, progreso, ct);

                MensajeEstado = resultado.PdfReparados > 0
                    ? $"Carga finalizada. {resultado.PdfReparados} PDF reparado(s), verificado(s) y enviado(s); los originales no cambiaron."
                    : $"Carga finalizada. {resultado.ArchivosEnviados} archivo(s) enviado(s) correctamente.";
            }
            catch (OperationCanceledException)
            {
                MarcarColaComoCancelada();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                MarcarColaComoCancelada();
            }
            catch (PdfValidationException ex)
            {
                MensajeEstado = ex.Message;

                foreach (var a in Archivos.Where(a =>
                             a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                             a.Estado == EstadoTransferenciaArchivo.EnCola))
                {
                    a.Estado = EstadoTransferenciaArchivo.Fallido;
                }

                MessageBox.Show(
                    ex.Message,
                    ex.Outcome == PdfInspectionOutcome.Encrypted
                        ? "PDF protegido o con contraseña"
                        : "El PDF no superó la validación",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                var mensaje = ManejadorErroresCliente.ObtenerMensajeUsuario(
                    ex,
                    "realizar la carga de archivos");

                MensajeEstado = mensaje;

                foreach (var a in Archivos.Where(a =>
                             a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                             a.Estado == EstadoTransferenciaArchivo.EnCola))
                {
                    a.Estado = EstadoTransferenciaArchivo.Fallido;
                }

                MessageBox.Show(
                    mensaje,
                    "Error en carga de archivos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _ctsSubida?.Dispose();
                _ctsSubida = null;
                SubidaEnProgreso = false;
                ActualizarHabilitacionComandos();
            }
        }

        private void MarcarColaComoCancelada()
        {
            MensajeEstado = "La cola de archivos fue cancelada.";

            foreach (var a in Archivos)
            {
                if (a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                    a.Estado == EstadoTransferenciaArchivo.EnCola)
                {
                    a.Estado = EstadoTransferenciaArchivo.Cancelado;
                }
            }
        }

        private async Task VerDocumentosAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var docs = await _servicioCarga.ObtenerDocumentosExistentesAsync(Contexto, cts.Token);

                if (docs == null || docs.Count == 0)
                {
                    MessageBox.Show(
                        "No se encontraron documentos registrados para este requerimiento.",
                        "Documentos",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var texto = string.Join(Environment.NewLine, docs);
                MessageBox.Show(
                    texto,
                    "Documentos registrados",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                var mensaje = ManejadorErroresCliente.ObtenerMensajeUsuario(
                    ex,
                    "consultar los documentos ya registrados");

                MensajeEstado = mensaje;

                MessageBox.Show(
                    mensaje,
                    "Error al consultar documentos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void PausarCola()
        {
            _ctsSubida?.Cancel();
        }

        private void OrdenarColaPorNombre()
        {
            ReordenarArchivos(Archivos.OrderBy(a => a.NombreArchivo,
                StringComparer.OrdinalIgnoreCase));
        }

        private void OrdenarColaPorTamanio()
        {
            ReordenarArchivos(Archivos.OrderBy(a => a.TamanioBytes));
        }

        private void ReordenarArchivos(IEnumerable<ModeloVistaArchivoTransferencia> nuevos)
        {
            var lista = nuevos.ToList();
            Archivos.Clear();
            foreach (var a in lista)
            {
                Archivos.Add(a);
            }
        }

        private void ActualizarHabilitacionComandos()
        {
            ComandoAgregarArchivos.ElevarCanExecuteChanged();
            ComandoAgregarCarpetas.ElevarCanExecuteChanged();
            ComandoQuitarArchivo.ElevarCanExecuteChanged();
            ComandoSubirArchivos.ElevarCanExecuteChanged();
            ComandoVerDocumentos.ElevarCanExecuteChanged();
            ComandoPausarCola.ElevarCanExecuteChanged();
            ComandoOrdenarPorNombre.ElevarCanExecuteChanged();
            ComandoOrdenarPorTamanio.ElevarCanExecuteChanged();
        }

        private bool _estaSubiendoArchivos;

        public bool EstaSubiendoArchivos
        {
            get => _estaSubiendoArchivos;
            set
            {
                if (EstablecerValor(ref _estaSubiendoArchivos, value))
                {
                    ActualizarHabilitacionComandos();
                }
            }
        }
    }
}
