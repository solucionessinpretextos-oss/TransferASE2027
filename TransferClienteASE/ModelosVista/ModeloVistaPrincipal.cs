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
using FileXfer.FileSafety;
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
        private readonly ServicioDiarioTransferencias _diario;

        private bool _subidaEnProgreso;
        private string _mensajeEstado = string.Empty;
        private string _etapaActual = "En espera";
        private string _archivoActual = "Ningún archivo en proceso";
        private double _progresoArchivoActual;
        private double _progresoTotal;
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

        // Respaldo seguro para los primeros milisegundos de la pantalla. En cuanto responde
        // el servidor, esta colección se reemplaza por la política efectiva versionada.
        private HashSet<string> _extensionesPermitidas = new(
            new[]
            {
                ".pdf", ".docx", ".xlsx", ".pptx", ".xltx", ".odt", ".ods", ".odp",
                ".ots", ".epub", ".xps", ".oxps", ".vsdx", ".xml", ".xsd", ".json",
                ".svg", ".svgz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif",
                ".tiff", ".txt", ".csv", ".tsv", ".tab", ".psv", ".md", ".yaml",
                ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".sql", ".ics",
                ".vcf", ".asc", ".csr", ".cer"
            },
            StringComparer.OrdinalIgnoreCase);

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

        public string EtapaActual
        {
            get => _etapaActual;
            set => EstablecerValor(ref _etapaActual, value);
        }

        public string ArchivoActual
        {
            get => _archivoActual;
            set => EstablecerValor(ref _archivoActual, value);
        }

        public double ProgresoArchivoActual
        {
            get => _progresoArchivoActual;
            set => EstablecerValor(ref _progresoArchivoActual, Math.Clamp(value, 0, 100));
        }

        public double ProgresoTotal
        {
            get => _progresoTotal;
            set => EstablecerValor(ref _progresoTotal, Math.Clamp(value, 0, 100));
        }

        public ComandoRelay ComandoAgregarArchivos { get; }
        public ComandoRelay ComandoAgregarCarpetas { get; }
        public ComandoRelay ComandoQuitarArchivo { get; }
        public ComandoRelay ComandoSubirArchivos { get; }
        public ComandoRelay ComandoVerDocumentos { get; }
        public ComandoRelay ComandoPausarCola { get; }
        public ComandoRelay ComandoOrdenarPorNombre { get; }
        public ComandoRelay ComandoOrdenarPorTamanio { get; }

        public ModeloVistaPrincipal(
            ServicioCargaArchivos servicioCarga,
            ContextoRequerimiento contexto,
            ServicioDiarioTransferencias? diario = null)
        {
            _servicioCarga = servicioCarga ?? throw new ArgumentNullException(nameof(servicioCarga));
            _contexto = contexto ?? throw new ArgumentNullException(nameof(contexto));
            _diario = diario ?? new ServicioDiarioTransferencias();

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

            _ = CargarPoliticasEfectivasAsync();
            _ = RestaurarColaPendienteAsync();
        }

        private async Task RestaurarColaPendienteAsync()
        {
            try
            {
                var pendientes = await _diario.ObtenerPendientesAsync(_contexto);
                foreach (var pendiente in pendientes)
                {
                    if (Archivos.Any(item => string.Equals(
                            item.RutaCompleta,
                            pendiente.OriginalPath,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var model = new ModeloVistaArchivoTransferencia(pendiente.OriginalPath)
                    {
                        Estado = EstadoTransferenciaArchivo.Pendiente,
                        Progreso = pendiente.PreparedLength > 0
                            ? Math.Clamp((double)pendiente.ConfirmedBytes / pendiente.PreparedLength * 100d, 0, 100)
                            : 0,
                        AccionActual = File.Exists(pendiente.OriginalPath) ||
                                       (!string.IsNullOrWhiteSpace(pendiente.PreparedPath) && File.Exists(pendiente.PreparedPath))
                            ? "Carga recuperada; lista para reconciliar con el servidor"
                            : "Esperando que se vuelva a seleccionar el archivo original",
                        ReciboId = pendiente.ReceiptId,
                        TraceId = pendiente.TraceId
                    };
                    Archivos.Add(model);
                }

                if (pendientes.Count > 0)
                {
                    var totalBytes = pendientes.Sum(item =>
                        item.PreparedLength > 0 ? item.PreparedLength : item.OriginalLength);
                    var confirmedBytes = pendientes.Sum(item => Math.Min(
                        item.ConfirmedBytes,
                        item.PreparedLength > 0 ? item.PreparedLength : item.OriginalLength));
                    ProgresoTotal = totalBytes > 0
                        ? (double)confirmedBytes / totalBytes * 100d
                        : 0;
                    EtapaActual = "Recuperación";
                    ArchivoActual = $"{pendientes.Count} archivo(s) pendiente(s)";
                    MensajeEstado = pendientes.Count == 1
                        ? "Se recuperó 1 carga pendiente. Pulse confirmar para continuar desde el último byte guardado."
                        : $"Se recuperaron {pendientes.Count} cargas pendientes. Pulse confirmar para continuar desde el último byte guardado.";
                }
            }
            catch (Exception exception)
            {
                MensajeEstado =
                    "No fue posible recuperar la cola anterior. Puede agregar archivos manualmente; " +
                    "el servidor seguirá protegiendo las cargas parciales.";
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }

        private async Task CargarPoliticasEfectivasAsync()
        {
            try
            {
                var policies = await _servicioCarga.ObtenerPoliticasEfectivasAsync();
                var extensions = policies
                    .Where(policy => policy.IsAllowed)
                    .Select(policy => policy.Extension.StartsWith(".", StringComparison.Ordinal)
                        ? policy.Extension
                        : "." + policy.Extension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (extensions.Count == 0)
                    return;

                _extensionesPermitidas = extensions;
                MensajeEstado = $"Políticas actualizadas: {extensions.Count} extensiones habilitadas por el servidor.";
            }
            catch (Exception exception)
            {
                // La validación autoritativa vuelve a ejecutarse al iniciar la carga. Aquí se
                // conserva un conjunto seguro para que una caída temporal no congele la UI.
                MensajeEstado = "No se pudieron actualizar las extensiones todavía; se usará la política segura local y se verificará nuevamente al enviar.";
                System.Diagnostics.Debug.WriteLine(exception);
            }
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
                _extensionesPermitidas.Contains(Path.GetExtension(r) ?? string.Empty));

            AgregarRutasACola(filtrados);
        }

        private void AgregarRutasACola(IEnumerable<string> rutas)
        {
            foreach (var ruta in rutas)
            {
                if (!File.Exists(ruta))
                    continue;

                if (!_extensionesPermitidas.Contains(Path.GetExtension(ruta) ?? string.Empty))
                    continue;

                if (!Archivos.Any(a =>
                        string.Equals(a.RutaCompleta, ruta, StringComparison.OrdinalIgnoreCase)))
                {
                    Archivos.Add(new ModeloVistaArchivoTransferencia(ruta));
                    _ = PersistirArchivoEnColaAsync(ruta);
                }
            }
        }

        private async Task PersistirArchivoEnColaAsync(string ruta)
        {
            try
            {
                await _diario.RegistrarEnColaAsync(_contexto, ruta);
            }
            catch (Exception exception)
            {
                MensajeEstado =
                    $"{Path.GetFileName(ruta)} fue agregado, pero no se pudo guardar su punto de recuperación local: {exception.Message}";
            }
        }

        private void QuitarArchivo(ModeloVistaArchivoTransferencia? archivo)
        {
            if (archivo == null)
                return;

            Archivos.Remove(archivo);
            _ = CancelarEntradaDiarioAsync(archivo.RutaCompleta);
        }

        private async Task CancelarEntradaDiarioAsync(string ruta)
        {
            try
            {
                await _diario.CancelarAsync(_contexto, ruta);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
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
            EtapaActual = "Preparación";
            ArchivoActual = "Organizando la cola de archivos";
            ProgresoArchivoActual = 0;
            ProgresoTotal = 0;

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
                        a.AccionActual = "En cola; pendiente de inspección";
                    }
                }

                var progreso = new Progress<ProgresoCargaArchivo>(p =>
                {
                    var modelo = Archivos.FirstOrDefault(a =>
                        string.Equals(a.RutaCompleta, p.RutaOriginal, StringComparison.OrdinalIgnoreCase));

                    if (modelo != null)
                    {
                        modelo.Progreso = p.PorcentajeArchivo;
                        modelo.AccionActual = $"{p.Etapa}: {p.Mensaje}";
                        if (!string.IsNullOrWhiteSpace(p.ReciboId))
                            modelo.ReciboId = p.ReciboId;
                        if (!string.IsNullOrWhiteSpace(p.TraceId))
                            modelo.TraceId = p.TraceId;
                        modelo.Estado = string.Equals(p.Etapa, "Completado", StringComparison.OrdinalIgnoreCase)
                            ? EstadoTransferenciaArchivo.Completado
                            : EstadoTransferenciaArchivo.Subiendo;
                    }

                    EtapaActual = p.Etapa;
                    ArchivoActual = p.NumeroArchivo <= 0
                        ? "Preparando políticas para la cola"
                        : $"Archivo {p.NumeroArchivo} de {p.TotalArchivos}: {Path.GetFileName(p.RutaOriginal)}";
                    ProgresoArchivoActual = p.PorcentajeArchivo;
                    ProgresoTotal = p.PorcentajeTotal;
                    MensajeEstado = p.Mensaje;
                });

                var resultado = await _servicioCarga.EnviarArchivosAsync(Contexto, rutas, progreso, ct);

                ProgresoArchivoActual = 100;
                ProgresoTotal = 100;
                EtapaActual = "Finalizado";
                ArchivoActual = $"{resultado.ArchivosEnviados} de {resultado.ArchivosEnviados} archivos procesados";
                var detalles = new List<string>
                {
                    $"{resultado.ArchivosEnviados} enviado(s)",
                    $"{resultado.ArchivosVerificados} verificado(s)",
                    $"{resultado.RecibosEmitidos.Count} recibo(s) digital(es)"
                };
                if (resultado.ArchivosReparados > 0)
                    detalles.Add($"{resultado.ArchivosReparados} reparado(s) y revalidado(s)");
                if (resultado.ArchivosReanudados > 0)
                    detalles.Add($"{resultado.ArchivosReanudados} reanudado(s)");
                if (resultado.VerificacionesOmitidas > 0)
                    detalles.Add($"{resultado.VerificacionesOmitidas} sin inspección profunda por política explícita");
                MensajeEstado = "Carga finalizada: " + string.Join(", ", detalles) + ". Los archivos originales no cambiaron.";
            }
            catch (OperationCanceledException)
            {
                MarcarColaComoCancelada();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                MarcarColaComoCancelada();
            }
            catch (FileValidationException ex)
            {
                MensajeEstado = ex.Message;
                EtapaActual = "Rechazado";

                foreach (var a in Archivos.Where(a =>
                             a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                             a.Estado == EstadoTransferenciaArchivo.EnCola))
                {
                    var eraArchivoActivo = a.Estado == EstadoTransferenciaArchivo.Subiendo;
                    a.Estado = EstadoTransferenciaArchivo.Fallido;
                    a.AccionActual = eraArchivoActivo
                        ? $"Rechazado: {ex.Message}"
                        : "No se procesó porque la cola fue detenida por un archivo rechazado";
                }

                MessageBox.Show(
                    ex.Message,
                    ex.Outcome == FileInspectionOutcome.Encrypted
                        ? "Archivo protegido o con contraseña"
                        : "El archivo no superó la validación",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                var mensaje = ManejadorErroresCliente.ObtenerMensajeUsuario(
                    ex,
                    "realizar la carga de archivos");

                MensajeEstado = mensaje;
                EtapaActual = "Error";

                foreach (var a in Archivos.Where(a =>
                             a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                             a.Estado == EstadoTransferenciaArchivo.EnCola))
                {
                    a.Estado = EstadoTransferenciaArchivo.Fallido;
                    a.AccionActual = $"Error: {mensaje}";
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
            EtapaActual = "Cancelado";

            foreach (var a in Archivos)
            {
                if (a.Estado == EstadoTransferenciaArchivo.Subiendo ||
                    a.Estado == EstadoTransferenciaArchivo.EnCola)
                {
                    a.Estado = EstadoTransferenciaArchivo.Cancelado;
                    a.AccionActual = "Carga cancelada por el usuario";
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
