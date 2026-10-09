// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/ModelosVista/ModeloVistaInicioSesion.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: ViewModel de inicio de sesión.
// Interacción: Gestiona selección de CER/KEY, validación de e.firma, login firmado y apertura del selector.
// Regla de mantenimiento: Respetar INotifyPropertyChanged y no bloquear UI thread; errores aquí se manifiestan como pantallas congeladas o bindings rotos.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;

namespace TransferClienteASE.ModelosVista
{
    public sealed class ModeloVistaInicioSesion : NotificacionPropiedadBase
    {
        private readonly ServicioConfiguracionCliente _servicioConfiguracion;
        private readonly ServicioVersion _servicioVersion;
        private readonly ServicioAutenticacion _servicioAutenticacion;
        private readonly IServicioDialogos _servicioDialogos;

        private string _rutaCertificadoCer = string.Empty;
        private string _rutaLlaveKey = string.Empty;
        private string _contrasena = string.Empty;
        private string _codigoMfa = string.Empty;
        private string _rfc = string.Empty;
        private string _mensajeEstado = string.Empty;
        private bool _estaProcesando;

        private string _certNombreCompleto = string.Empty;
        private string _certRfc = string.Empty;
        private string _certCurp = string.Empty;
        private string _certSerie = string.Empty;
        private string _certVigencia = string.Empty;

        public string RutaCertificadoCer
        {
            get => _rutaCertificadoCer;
            set
            {
                if (EstablecerValor(ref _rutaCertificadoCer, value))
                {
                    ComandoIngresar.ElevarCanExecuteChanged();
                    ActualizarRfcDesdeCertificado();
                }
            }
        }

        public string RutaLlaveKey
        {
            get => _rutaLlaveKey;
            set
            {
                if (EstablecerValor(ref _rutaLlaveKey, value))
                {
                    ComandoIngresar.ElevarCanExecuteChanged();
                }
            }
        }

        public string Contrasena
        {
            get => _contrasena;
            set
            {
                if (EstablecerValor(ref _contrasena, value))
                {
                    ComandoIngresar.ElevarCanExecuteChanged();
                }
            }
        }

        public string CodigoMfa
        {
            get => _codigoMfa;
            set => EstablecerValor(ref _codigoMfa, value);
        }

        public string Rfc
        {
            get => _rfc;
            private set => EstablecerValor(ref _rfc, value);
        }

        public string Ambiente => _servicioConfiguracion.Ambiente;

        public string VersionCliente
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v?.ToString() ?? "0.0.0.0";
            }
        }

        public string MensajeEstado
        {
            get => _mensajeEstado;
            set => EstablecerValor(ref _mensajeEstado, value);
        }

        public bool EstaProcesando
        {
            get => _estaProcesando;
            set
            {
                if (EstablecerValor(ref _estaProcesando, value))
                {
                    ComandoIngresar.ElevarCanExecuteChanged();
                }
            }
        }

        public string CertNombreCompleto
        {
            get => _certNombreCompleto;
            private set => EstablecerValor(ref _certNombreCompleto, value);
        }

        public string CertRfc
        {
            get => _certRfc;
            private set => EstablecerValor(ref _certRfc, value);
        }

        public string CertCurp
        {
            get => _certCurp;
            private set => EstablecerValor(ref _certCurp, value);
        }

        public string CertSerie
        {
            get => _certSerie;
            private set => EstablecerValor(ref _certSerie, value);
        }

        public string CertVigencia
        {
            get => _certVigencia;
            private set => EstablecerValor(ref _certVigencia, value);
        }

        public ComandoRelay ComandoExaminarCer { get; }
        public ComandoRelay ComandoExaminarKey { get; }
        public ComandoRelay ComandoIngresar { get; }
        public ComandoRelay ComandoCerrar { get; }

        public event EventHandler<string>? InicioSesionExitoso;

        public ModeloVistaInicioSesion(
            ServicioConfiguracionCliente servicioConfiguracion,
            ServicioVersion servicioVersion,
            ServicioAutenticacion servicioAutenticacion,
            IServicioDialogos servicioDialogos)
        {
            _servicioConfiguracion = servicioConfiguracion ?? throw new ArgumentNullException(nameof(servicioConfiguracion));
            _servicioVersion = servicioVersion ?? throw new ArgumentNullException(nameof(servicioVersion));
            _servicioAutenticacion = servicioAutenticacion ?? throw new ArgumentNullException(nameof(servicioAutenticacion));
            _servicioDialogos = servicioDialogos ?? throw new ArgumentNullException(nameof(servicioDialogos));

            ComandoExaminarCer = new ComandoRelay(_ => ExaminarArchivoCer());
            ComandoExaminarKey = new ComandoRelay(_ => ExaminarArchivoKey());
            ComandoIngresar = new ComandoRelay(async _ => await IngresarAsync(), _ => PuedeIngresar());
            ComandoCerrar = new ComandoRelay(_ => Application.Current.Shutdown());
        }

        private void ExaminarArchivoCer()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Certificados (*.cer)|*.cer|Todos los archivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                RutaCertificadoCer = dlg.FileName;
            }
        }

        private void ExaminarArchivoKey()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Llaves privadas (*.key)|*.key|Todos los archivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                RutaLlaveKey = dlg.FileName;
            }
        }

        private bool PuedeIngresar()
        {
            if (EstaProcesando) return false;

            return !string.IsNullOrWhiteSpace(RutaCertificadoCer)
                   && !string.IsNullOrWhiteSpace(RutaLlaveKey)
                   && !string.IsNullOrWhiteSpace(Contrasena);
        }

        public async Task RevisarVersionAlInicioAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var res = await _servicioVersion.RevisarVersionAsync(cts.Token);

                if (!string.IsNullOrEmpty(res.MensajeError))
                {
                    MensajeEstado = res.MensajeError;
                    return;
                }

                if (!res.HayNuevaVersion)
                {
                    MensajeEstado = $"Versión actual: {res.VersionActual}. No hay actualizaciones pendientes.";
                    return;
                }

                var mensaje = $"Se detectó una nueva versión del sistema de transferencia.\n\n" +
                              $"Versión instalada: {res.VersionActual}\n" +
                              $"Versión disponible: {res.VersionDisponible}";

                if (!string.IsNullOrWhiteSpace(res.NotasVersion))
                {
                    mensaje += "\n\nNovedades:\n" + res.NotasVersion;
                }

                bool debeActualizar;

                if (res.EsActualizacionObligatoria)
                {
                    mensaje += "\n\nEsta actualización es obligatoria para poder continuar usando el sistema. " +
                               "Se descargará e instalará a continuación.";

                    _servicioDialogos.MostrarMensajeImportante(
                        "Actualización obligatoria",
                        mensaje,
                        TipoDialogo.Advertencia);

                    debeActualizar = true;
                }
                else
                {
                    mensaje += "\n\n¿Desea descargar e instalarla ahora?";

                    debeActualizar = _servicioDialogos.MostrarConfirmacion(
                        "Actualización disponible",
                        mensaje,
                        TipoDialogo.Informacion);
                }

                if (!debeActualizar)
                {
                    MensajeEstado = "Puede continuar usando la versión actual. Se recomienda actualizar en cuanto sea posible.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(res.UrlDescarga))
                {
                    MensajeEstado = "El servidor indicó que existe una nueva versión, pero no proporcionó la ruta de descarga. " +
                                    "Notifique a Soporte ASE.";
                    _servicioDialogos.MostrarMensajeImportante(
                        "Error en actualización",
                        MensajeEstado,
                        TipoDialogo.Error);
                    return;
                }

                using var ctsDescarga = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                MensajeEstado = "Descargando actualización del sistema, por favor espere...";

                var resultadoDescarga = await _servicioVersion.DescargarEInstalarAsync(
                    res,
                    ctsDescarga.Token);

                if (!resultadoDescarga.Exito)
                {
                    MensajeEstado = resultadoDescarga.MensajeError
                                    ?? "No fue posible completar la actualización. Notifique a Soporte ASE.";

                    _servicioDialogos.MostrarMensajeImportante(
                        "Error al actualizar",
                        MensajeEstado,
                        TipoDialogo.Error);

                    return;
                }

                MensajeEstado = "La actualización se descargó correctamente. "
                                + "El instalador se abrirá a continuación.";

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MensajeEstado = $"No se pudo verificar si hay una nueva versión. Puede seguir utilizando el sistema. " +
                                $"Detalle: {ex.Message}";

                _servicioDialogos.MostrarMensajeImportante(
                    "Error al verificar versión",
                    MensajeEstado,
                    TipoDialogo.Error);
            }
        }

        private async Task IngresarAsync()
        {
            EstaProcesando = true;
            MensajeEstado = "Validando e.firma y RFC en el SAT...";
            string? rfcInicioExitoso = null;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var resultado = await _servicioAutenticacion.IniciarSesionAsync(
                    RutaCertificadoCer,
                    RutaLlaveKey,
                    Contrasena,
                    CodigoMfa,
                    cts.Token);

                if (!resultado.EsCorrecto)
                {
                    MensajeEstado = resultado.MensajeError ?? "No fue posible iniciar sesión.";

                    _servicioDialogos.MostrarMensajeImportante(
                        "No fue posible iniciar sesión",
                        MensajeEstado,
                        TipoDialogo.Error);

                    return;
                }

                if (!string.IsNullOrWhiteSpace(resultado.RfcNormalizado))
                {
                    Rfc = resultado.RfcNormalizado!;
                }
                else if (!string.IsNullOrWhiteSpace(SesionAplicacion.Rfc))
                {
                    Rfc = SesionAplicacion.Rfc;
                }

                CertNombreCompleto = string.Empty;
                CertRfc = string.Empty;
                CertCurp = string.Empty;
                CertSerie = string.Empty;
                CertVigencia = string.Empty;

                if (resultado.Certificado is { } cert)
                {
                    if (!string.IsNullOrWhiteSpace(cert.NombreCompleto))
                        CertNombreCompleto = cert.NombreCompleto!;

                    if (!string.IsNullOrWhiteSpace(cert.Rfc))
                        CertRfc = cert.Rfc!.ToUpperInvariant();

                    if (!string.IsNullOrWhiteSpace(cert.Curp))
                        CertCurp = cert.Curp!.ToUpperInvariant();

                    if (!string.IsNullOrWhiteSpace(cert.Serie))
                        CertSerie = cert.Serie!;

                    if (cert.NotBefore.HasValue && cert.NotAfter.HasValue)
                    {
                        CertVigencia =
                            $"{cert.NotBefore.Value:dd/MM/yyyy} - {cert.NotAfter.Value:dd/MM/yyyy}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(CertNombreCompleto))
                {
                    SesionAplicacion.NombreUsuario = CertNombreCompleto.Trim();
                    MensajeEstado =
                        $"Inicio de sesión exitoso.\n" +
                        $"Titular: {CertNombreCompleto}\n" +
                        $"RFC: {Rfc}\n" +
                        (string.IsNullOrWhiteSpace(CertVigencia)
                            ? string.Empty
                            : $"Vigencia del certificado: {CertVigencia}");
                }
                else
                {
                    MensajeEstado = $"Inicio de sesión exitoso. RFC: {Rfc}";
                }

                rfcInicioExitoso = Rfc;
            }
            catch (Exception ex)
            {
                MensajeEstado = $"Error al iniciar sesión: {ex.Message}";

                _servicioDialogos.MostrarMensajeImportante(
                    "Error al iniciar sesión",
                    MensajeEstado,
                    TipoDialogo.Error);
            }
            finally
            {
                EstaProcesando = false;
            }

            if (!string.IsNullOrWhiteSpace(rfcInicioExitoso))
            {
                InicioSesionExitoso?.Invoke(this, rfcInicioExitoso);
            }
        }

        private void ActualizarRfcDesdeCertificado()
        {
            Rfc = string.Empty;

            if (string.IsNullOrWhiteSpace(RutaCertificadoCer) || !File.Exists(RutaCertificadoCer))
                return;

            try
            {
                var cerBytes = File.ReadAllBytes(RutaCertificadoCer);
                var cert = new X509Certificate2(cerBytes);
                var subject = cert.Subject ?? string.Empty;

                const string prefijoOid = "OID.2.5.4.45=";
                var idx = subject.IndexOf(prefijoOid, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var resto = subject[(idx + prefijoOid.Length)..];
                    var fin = resto.IndexOf(',');
                    var valor = (fin >= 0 ? resto[..fin] : resto).Trim();
                    if (!string.IsNullOrWhiteSpace(valor))
                    {
                        Rfc = valor.ToUpperInvariant();
                        MensajeEstado = $"RFC detectado en certificado: {Rfc}";
                        return;
                    }
                }

                MensajeEstado = "No se encontró el RFC en OID.2.5.4.45 del certificado.";

                _servicioDialogos.MostrarMensajeImportante(
                    "RFC no detectado",
                    MensajeEstado,
                    TipoDialogo.Advertencia);
            }
            catch (Exception ex)
            {
                MensajeEstado = $"Error al leer el certificado: {ex.Message}";

                _servicioDialogos.MostrarMensajeImportante(
                    "Error al leer el certificado",
                    MensajeEstado,
                    TipoDialogo.Error);
            }
        }
    }
}
