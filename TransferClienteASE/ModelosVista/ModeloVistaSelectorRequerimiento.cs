// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/ModelosVista/ModeloVistaSelectorRequerimiento.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: ViewModel del selector principal de contexto.
// Interacción: Carga combos en cascada y construye ContextoRequerimiento según acto normal, acto 16 o acto 21.
// Regla de mantenimiento: Respetar INotifyPropertyChanged y no bloquear UI thread; errores aquí se manifiestan como pantallas congeladas o bindings rotos.
// Notas específicas:
// - Este archivo decide qué flujo mostrar según el acto seleccionado; cualquier acto nuevo debe integrarse aquí y en backend.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Reflection;
using System.Globalization;
using System.Text;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;
using TransferClienteASE.Vistas;

using AnioDto = TransferClienteASE.Servicios.AnioDto;
using EnteDto = TransferClienteASE.Servicios.EnteDto;
using ActoDto = TransferClienteASE.Servicios.ActoDto;
using OficioDto = TransferClienteASE.Servicios.OficioDto;
using RequerimientoDto = TransferClienteASE.Servicios.RequerimientoDto;
using PagedResultReq = TransferClienteASE.Servicios.PagedResult<TransferClienteASE.Servicios.RequerimientoDto>;
using AnexoCedulaDto = TransferClienteASE.Servicios.AnexoCedulaDto;
using ResultadoCedulaDto = TransferClienteASE.Servicios.ResultadoCedulaDto;
using ResultadoInformeDto = TransferClienteASE.Servicios.ResultadoInformeDto;


// ============================================================================
//  ModeloVistaSelectorRequerimiento
// ============================================================================
//  Orquesta la pantalla principal de selección:
//      Año -> Ente -> Acto -> Oficio -> Requerimiento/Resultado.
//
//  Esta clase decide qué flujo mostrar según el acto seleccionado:
//  - Acto normal: muestra requerimientos.
//  - Cédula de Resultados Preliminares: muestra anexos y resultados de BUZ_Resultados_Cedulas.
//  - Informe Individual de Auditoría: muestra oficios y resultados asignados desde
//    BUZ_Asignaciones_Resultados_Informe / BUZ_Resultados_CedulaEvaluacion.
//
//  La vista no debe consultar servicios directamente; debe hacerlo a través de este ViewModel.
// ============================================================================
namespace TransferClienteASE.ModelosVista
{
    public sealed class ModeloVistaSelectorRequerimiento : NotificacionPropiedadBase
    {
        private readonly ServicioCatalogosAuditoria _servicio;
        private readonly ServicioCatalogosAuditoria _servicioCatalogos;
        private readonly string _rfcUsuario;
        private CancellationTokenSource? _cts;

        public string RfcUsuario => _rfcUsuario;

        public string NombreUsuario => SesionAplicacion.NombreUsuario ?? string.Empty;

        private string _mensajeEstado = string.Empty;
        public string MensajeEstado
        {
            get => _mensajeEstado;
            set => EstablecerValor(ref _mensajeEstado, value);
        }

        public bool PuedeVerArchivosLocales =>
            !Cargando &&
            (
                EsActoCedula
                    ? ResultadoCedulaSeleccionado is not null
                    : EsActoInformeIndividual
                        ? ResultadoInformeSeleccionado is not null
                        : RequerimientoSeleccionado is not null
            );

        public ObservableCollection<AnioDto> Anios { get; } = new();
        public ObservableCollection<EnteDto> Entes { get; } = new();
        public ObservableCollection<ActoDto> Actos { get; } = new();
        public ObservableCollection<OficioDto> Oficios { get; } = new();
        public ObservableCollection<RequerimientoDto> Requerimientos { get; } = new();

        public ObservableCollection<AnexoCedulaDto> AnexosCedula { get; } = new();
        public ObservableCollection<ResultadoCedulaDto> ResultadosCedula { get; } = new();
        public ObservableCollection<ResultadoInformeDto> ResultadosInforme { get; } = new();

        private ResultadoInformeDto? _resultadoInformeSeleccionado;
        public ResultadoInformeDto? ResultadoInformeSeleccionado
        {
            get => _resultadoInformeSeleccionado;
            set
            {
                if (EstablecerValor(ref _resultadoInformeSeleccionado, value))
                {
                    NotificarCambio(nameof(PuedeContinuar));
                    ComandoContinuar.ElevarCanExecuteChanged();
                    NotificarCambio(nameof(PuedeVerArchivosLocales));
                    ComandoVerArchivosLocales.ElevarCanExecuteChanged();
                }
            }
        }

        private AnexoCedulaDto? _anexoSeleccionado;
        public AnexoCedulaDto? AnexoSeleccionado
        {
            get => _anexoSeleccionado;
            set
            {
                if (EstablecerValor(ref _anexoSeleccionado, value))
                {
                    _ = RefrescarResultadosCedulaAsync();
                    NotificarCambio(nameof(PuedeContinuar));
                    ComandoContinuar.ElevarCanExecuteChanged();
                }
            }
        }

        private ResultadoCedulaDto? _resultadoCedulaSeleccionado;
        public ResultadoCedulaDto? ResultadoCedulaSeleccionado
        {
            get => _resultadoCedulaSeleccionado;
            set
            {
                if (EstablecerValor(ref _resultadoCedulaSeleccionado, value))
                {
                    NotificarCambio(nameof(PuedeContinuar));
                    ComandoContinuar.ElevarCanExecuteChanged();
                    NotificarCambio(nameof(PuedeVerArchivosLocales));
                    ComandoVerArchivosLocales.ElevarCanExecuteChanged();
                }
            }
        }

        private AnioDto? _anioSeleccionado;
        public AnioDto? AnioSeleccionado
        {
            get => _anioSeleccionado;
            set
            {
                if (EstablecerValor(ref _anioSeleccionado, value))
                {
                    _ = RefrescarEntesAsync();
                }
            }
        }

        private EnteDto? _enteSeleccionado;
        public EnteDto? EnteSeleccionado
        {
            get => _enteSeleccionado;
            set
            {
                if (EstablecerValor(ref _enteSeleccionado, value))
                {
                    NotificarCambio(nameof(NombreEnteSeleccionado));
                    _ = RefrescarActosAsync();
                }
            }
        }

        public string NombreEnteSeleccionado =>
            EnteSeleccionado?.EnteFiscalizado ?? string.Empty;

        private ActoDto? _actoSeleccionado;
        public ActoDto? ActoSeleccionado
        {
            get => _actoSeleccionado;
            set
            {
                if (EstablecerValor(ref _actoSeleccionado, value))
                {
                    NotificarCambio(nameof(EsActoCedula));
                    NotificarCambio(nameof(EsActoInformeIndividual));
                    NotificarCambio(nameof(EsActoNormal));
                    NotificarCambio(nameof(UsaResultados));
                    NotificarCambio(nameof(MuestraOficio));
                    NotificarCambio(nameof(TituloRequerimientos));
                    NotificarCambio(nameof(TextoFiltroRequerimientos));

                    Oficios.Clear();
                    OficioSeleccionado = null;

                    Requerimientos.Clear();
                    RequerimientoSeleccionado = null;

                    AnexosCedula.Clear();
                    ResultadosCedula.Clear();
                    ResultadosInforme.Clear();
                    AnexoSeleccionado = null;
                    ResultadoCedulaSeleccionado = null;
                    ResultadoInformeSeleccionado = null;

                    TotalRequerimientos = 0;
                    PaginaActual = 1;

                    if (EsActoCedula)
                    {
                        _ = RefrescarAnexosCedulaAsync();
                    }
                    else
                    {
                        _ = RefrescarOficiosAsync();
                    }
                }
            }
        }

        private OficioDto? _oficioSeleccionado;
        public OficioDto? OficioSeleccionado
        {
            get => _oficioSeleccionado;
            set
            {
                if (EstablecerValor(ref _oficioSeleccionado, value))
                {
                    if (EsActoInformeIndividual)
                    {
                        _ = RefrescarResultadosInformeAsync();
                    }
                    else
                    {
                        _ = RefrescarRequerimientosAsync();
                    }
                }
            }
        }

        private RequerimientoDto? _requerimientoSeleccionado;
        public RequerimientoDto? RequerimientoSeleccionado
        {
            get => _requerimientoSeleccionado;
            set
            {
                if (EstablecerValor(ref _requerimientoSeleccionado, value))
                {
                    NotificarCambio(nameof(PuedeContinuar));
                    NotificarCambio(nameof(PuedeVerArchivosLocales));
                    ComandoContinuar.ElevarCanExecuteChanged();
                    ComandoVerArchivosLocales.ElevarCanExecuteChanged();
                }
            }
        }

        // Detecta el flujo del acto 16. Se usa búsqueda por texto para no depender de IDs
        // diferentes entre ambientes. Si el catálogo cambia de nombre, revisar esta condición.
        public bool EsActoCedula
        {
            get
            {
                var nom = Normalize(ActoSeleccionado?.Nom_Acto);
                if (string.IsNullOrWhiteSpace(nom)) return false;

                if (nom.Contains("EVALUACION"))
                    return false;

                return (nom.Contains("CEDULA") && nom.Contains("RESULTADOS"))
                       || nom.Contains("CRP");
            }
        }

        // Detecta el flujo del acto 21 Informe Individual de Auditoría.
        // Cuando esto es true, el grid principal se llena con ResultadosInforme y la carga
        // se cataloga contra BUZ_Resultados_CedulaEvaluacion.ID.
        public bool EsActoInformeIndividual
        {
            get
            {
                var nom = Normalize(ActoSeleccionado?.Nom_Acto);
                if (string.IsNullOrWhiteSpace(nom)) return false;

                return nom.Contains("INFORME INDIVIDUAL")
                       && (nom.Contains("AUDITORIA") || nom.Contains("AUDITORÍA"));
            }
        }

        public bool UsaResultados => EsActoCedula || EsActoInformeIndividual;

        public bool MuestraOficio => !EsActoCedula;

        public bool EsActoNormal => !EsActoCedula && !EsActoInformeIndividual;

        private string _filtroTexto = string.Empty;
        public string FiltroTexto
        {
            get => _filtroTexto;
            set
            {
                if (EstablecerValor(ref _filtroTexto, value))
                {
                    _ = RefrescarRequerimientosAsync();
                }
            }
        }

        private int _paginaActual = 1;
        public int PaginaActual
        {
            get => _paginaActual;
            set
            {
                if (EstablecerValor(ref _paginaActual, value))
                {
                    ComandoPaginaAnterior.ElevarCanExecuteChanged();
                    ComandoPaginaSiguiente.ElevarCanExecuteChanged();
                }
            }
        }

        private int _tamanoPagina = 50;
        public int TamanoPagina
        {
            get => _tamanoPagina;
            set
            {
                if (EstablecerValor(ref _tamanoPagina, value))
                {
                    NotificarCambio(nameof(TamanoPagina));
                    NotificarCambio(nameof(TotalPaginas));
                }
            }
        }

        private int _totalRequerimientos;
        public int TotalRequerimientos
        {
            get => _totalRequerimientos;
            set
            {
                if (EstablecerValor(ref _totalRequerimientos, value))
                {
                    NotificarCambio(nameof(TotalPaginas));
                    ComandoPaginaAnterior.ElevarCanExecuteChanged();
                    ComandoPaginaSiguiente.ElevarCanExecuteChanged();
                }
            }
        }

        public int TotalPaginas =>
            (int)Math.Ceiling((double)TotalRequerimientos / Math.Max(1, TamanoPagina));

        private bool _cargando;
        public bool Cargando
        {
            get => _cargando;
            set
            {
                if (EstablecerValor(ref _cargando, value))
                {
                    ComandoRefrescar.ElevarCanExecuteChanged();
                    ComandoPaginaAnterior.ElevarCanExecuteChanged();
                    ComandoPaginaSiguiente.ElevarCanExecuteChanged();
                    ComandoContinuar.ElevarCanExecuteChanged();
                    ComandoCancelar.ElevarCanExecuteChanged();
                    ComandoVerArchivosLocales.ElevarCanExecuteChanged();
                    NotificarCambio(nameof(PuedeVerArchivosLocales));
                }
            }
        }

        public bool PuedeContinuar =>
                !Cargando &&
                AnioSeleccionado != null &&
                EnteSeleccionado != null &&
                ActoSeleccionado != null &&
                (
                    EsActoCedula
                        ? AnexoSeleccionado != null && ResultadoCedulaSeleccionado != null
                        : EsActoInformeIndividual
                            ? OficioSeleccionado != null && ResultadoInformeSeleccionado != null
                            : OficioSeleccionado != null && RequerimientoSeleccionado != null
                );

        public string TituloRequerimientos =>
            UsaResultados
                ? "Resultados asignados por atender"
                : "Requerimientos asignados por atender";

        public string TextoFiltroRequerimientos =>
            UsaResultados
                ? "Filtrar resultados:"
                : "Filtrar requerimientos:";

        public ComandoAsync ComandoRefrescar { get; }
        public ComandoAsync ComandoPaginaAnterior { get; }
        public ComandoAsync ComandoPaginaSiguiente { get; }
        public ComandoSimple ComandoCancelar { get; }
        public ComandoSimple ComandoContinuar { get; }
        public ComandoAsync ComandoVerArchivosLocales { get; }

        public event EventHandler<ContextoRequerimiento>? RequerimientoConfirmado;
        public event EventHandler? Cancelado;

        public ModeloVistaSelectorRequerimiento(
            ServicioCatalogosAuditoria servicioCatalogos,
            string rfcUsuario)
        {
            _servicio = servicioCatalogos ?? throw new ArgumentNullException(nameof(servicioCatalogos));
            _servicioCatalogos = _servicio;
            _rfcUsuario = string.IsNullOrWhiteSpace(rfcUsuario)
                ? throw new ArgumentNullException(nameof(rfcUsuario))
                : rfcUsuario.Trim().ToUpperInvariant();

            ComandoRefrescar = new ComandoAsync(
                async _ => await RefrescarTodoAsync(),
                _ => !Cargando);

            ComandoPaginaAnterior = new ComandoAsync(
                async _ => await CambiarPaginaAsync(-1),
                _ => !Cargando && PaginaActual > 1);

            ComandoPaginaSiguiente = new ComandoAsync(
                async _ => await CambiarPaginaAsync(+1),
                _ => !Cargando && PaginaActual < TotalPaginas);

            ComandoCancelar = new ComandoSimple(
                _ => Cancelado?.Invoke(this, EventArgs.Empty),
                _ => !Cargando);

            ComandoContinuar = new ComandoSimple(
                _ => ConfirmarSeleccion(),
                _ => PuedeContinuar);

            ComandoVerArchivosLocales = new ComandoAsync(
                    async _ => await VerArchivosLocalesAsync(),
                    _ => PuedeVerArchivosLocales);

            _ = CargarAniosAsync();
        }

        public async Task ActualizarTamanoPaginaAsync(int nuevoTamanoPagina)
        {
            if (nuevoTamanoPagina <= 0)
                nuevoTamanoPagina = 1;

            if (TamanoPagina == nuevoTamanoPagina)
                return;

            TamanoPagina = nuevoTamanoPagina;

            if (!EsActoNormal)
                return;

            if (OficioSeleccionado is null || EnteSeleccionado is null)
                return;

            if (PaginaActual > TotalPaginas)
                PaginaActual = TotalPaginas == 0 ? 1 : TotalPaginas;

            await RefrescarRequerimientosAsync(resetPagina: false);
        }

        public async Task RefrescarRequerimientoDespuesDeSubirAsync()
        {
            if (EsActoCedula)
            {
                if (EnteSeleccionado is null || ActoSeleccionado is null || AnexoSeleccionado is null || ResultadoCedulaSeleccionado is null)
                    return;

                var idResultadoSeleccionado = ResultadoCedulaSeleccionado.ID;

                await RefrescarResultadosCedulaAsync();

                var actualizado = ResultadosCedula.FirstOrDefault(r => r.ID == idResultadoSeleccionado);
                if (actualizado is not null)
                    ResultadoCedulaSeleccionado = actualizado;

                return;
            }

            if (EsActoInformeIndividual)
            {
                if (EnteSeleccionado is null || OficioSeleccionado is null || ResultadoInformeSeleccionado is null)
                    return;

                var idResultadoSeleccionado = ResultadoInformeSeleccionado.ID;

                await RefrescarResultadosInformeAsync();

                var actualizado = ResultadosInforme.FirstOrDefault(r => r.ID == idResultadoSeleccionado);
                if (actualizado is not null)
                    ResultadoInformeSeleccionado = actualizado;

                return;
            }

            if (OficioSeleccionado is null || EnteSeleccionado is null || RequerimientoSeleccionado is null)
                return;

            var idReqSeleccionado = RequerimientoSeleccionado.IdRequerimiento;

            await RefrescarRequerimientosAsync(resetPagina: false);

            var reqActualizado = Requerimientos
                .FirstOrDefault(r => r.IdRequerimiento == idReqSeleccionado);

            if (reqActualizado is not null)
            {
                RequerimientoSeleccionado = reqActualizado;
            }
        }

        private CancellationToken NuevoToken()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            return _cts.Token;
        }

        private async Task CargarAniosAsync()
        {
            try
            {
                Cargando = true;

                Anios.Clear();
                Entes.Clear();
                Actos.Clear();
                Oficios.Clear();
                Requerimientos.Clear();

                AnexosCedula.Clear();
                ResultadosCedula.Clear();
                ResultadosInforme.Clear();
                AnexoSeleccionado = null;
                ResultadoCedulaSeleccionado = null;
                ResultadoInformeSeleccionado = null;

                AnioSeleccionado = null;
                EnteSeleccionado = null;
                ActoSeleccionado = null;
                OficioSeleccionado = null;
                RequerimientoSeleccionado = null;

                TotalRequerimientos = 0;
                PaginaActual = 1;

                var anios = await _servicio.ObtenerAniosAsync();

                foreach (var a in anios.OrderByDescending(a => a.Id))
                    Anios.Add(a);

                if (Anios.Count == 0)
                {
                    AnioSeleccionado = null;
                    return;
                }

                var anioConEntes = await DetectarPrimerAnioConEntesAsync(Anios);

                AnioSeleccionado = anioConEntes ?? Anios.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar años:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private async Task<AnioDto?> DetectarPrimerAnioConEntesAsync(IEnumerable<AnioDto> anios)
        {
            foreach (var anio in anios)
            {
                try
                {
                    var ct = NuevoToken();
                    var entes = await _servicio.ObtenerEntesAsync(_rfcUsuario, anio.Id, ct);

                    if (entes != null && entes.Any())
                        return anio;
                }
                catch (TaskCanceledException)
                {
                    continue;
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private async Task RefrescarTodoAsync()
        {
            await CargarAniosAsync();
        }

        private async Task RefrescarEntesAsync()
        {
            try
            {
                Cargando = true;

                Entes.Clear();
                EnteSeleccionado = null;

                Actos.Clear();
                ActoSeleccionado = null;

                Oficios.Clear();
                OficioSeleccionado = null;

                Requerimientos.Clear();
                RequerimientoSeleccionado = null;

                AnexosCedula.Clear();
                ResultadosCedula.Clear();
                ResultadosInforme.Clear();
                AnexoSeleccionado = null;
                ResultadoCedulaSeleccionado = null;
                ResultadoInformeSeleccionado = null;

                TotalRequerimientos = 0;
                PaginaActual = 1;

                if (AnioSeleccionado is null)
                    return;

                var periodo = AnioSeleccionado.Id;
                var ct = NuevoToken();

                var entes = await _servicio.ObtenerEntesAsync(_rfcUsuario, periodo, ct);

                foreach (var e in entes)
                    Entes.Add(e);

                if (Entes.Count == 0)
                {
                    var dialogo = new DialogoImportanteWindow(
                        "Sin auditorías asignadas",
                        "No se encontraron auditorías vigentes asociadas a su RFC.\n\n" +
                        "Si considera que esto es un error, contacte al área de Auditoría.",
                        TipoDialogo.Advertencia)
                    {
                        Owner = Application.Current.MainWindow
                    };

                    dialogo.ShowDialog();

                    Cancelado?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (Entes.Count == 1)
                {
                    EnteSeleccionado = Entes[0];
                    return;
                }

                EnteSeleccionado = Entes.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar entes:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private async Task RefrescarActosAsync()
        {
            try
            {
                Cargando = true;

                Actos.Clear();
                ActoSeleccionado = null;

                Oficios.Clear();
                OficioSeleccionado = null;

                Requerimientos.Clear();
                RequerimientoSeleccionado = null;

                AnexosCedula.Clear();
                ResultadosCedula.Clear();
                ResultadosInforme.Clear();
                AnexoSeleccionado = null;
                ResultadoCedulaSeleccionado = null;
                ResultadoInformeSeleccionado = null;

                TotalRequerimientos = 0;
                PaginaActual = 1;

                if (AnioSeleccionado is null || EnteSeleccionado is null)
                    return;

                var ct = NuevoToken();

                var actos = await _servicio.ObtenerActosAsync(ct);

                foreach (var a in actos)
                    Actos.Add(a);

                ActoSeleccionado = Actos.FirstOrDefault(a => a.ID_Acto == 1) ?? Actos.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar actos:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        /// <summary>
        /// Recarga el combo Oficio de Solicitud cuando cambia el acto o la auditoría.
        ///
        /// Para Cédula de Resultados Preliminares no se requiere oficio, por eso se limpia.
        /// Para Informe Individual sí se consulta oficio porque las asignaciones del acto 21
        /// están amarradas a ID_Movimiento.
        /// </summary>
        private async Task RefrescarOficiosAsync()
        {
            if (EsActoCedula)
                return;

            try
            {
                Cargando = true;

                Oficios.Clear();
                OficioSeleccionado = null;

                Requerimientos.Clear();
                RequerimientoSeleccionado = null;
                ResultadosInforme.Clear();
                ResultadoInformeSeleccionado = null;

                TotalRequerimientos = 0;
                PaginaActual = 1;

                if (AnioSeleccionado is null || EnteSeleccionado is null || ActoSeleccionado is null)
                    return;

                var idAuditoriaStr = EnteSeleccionado.IdAuditoria ?? string.Empty;

                if (!int.TryParse(idAuditoriaStr, out var idAuditoriaInt))
                {
                    MessageBox.Show(
                        $"El IdAuditoria recibido ('{idAuditoriaStr}') no es numérico.",
                        "Error en datos de auditoría",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return;
                }

                var idActo = ActoSeleccionado.ID_Acto;
                var idColabora = EnteSeleccionado.IdColabora;

                var ct = NuevoToken();

                var oficios = await _servicio.ObtenerOficiosAsync(
                    AnioSeleccionado!.Id,
                    idAuditoriaInt,
                    idActo,
                    idColabora,
                    ct);

                foreach (var o in oficios)
                    Oficios.Add(o);

                OficioSeleccionado = Oficios.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar oficios:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private async Task RefrescarRequerimientosAsync(bool resetPagina = true)
        {
            if (!EsActoNormal)
                return;

            try
            {
                Cargando = true;

                Requerimientos.Clear();
                RequerimientoSeleccionado = null;

                if (resetPagina)
                    PaginaActual = 1;

                if (AnioSeleccionado is null || EnteSeleccionado is null ||
                    ActoSeleccionado is null || OficioSeleccionado is null)
                {
                    TotalRequerimientos = 0;
                    return;
                }

                var idOficio = OficioSeleccionado.IdOficio;
                var idColabora = EnteSeleccionado.IdColabora;
                if (!TryGetIdAuditoria(EnteSeleccionado, out var idAuditoria))
                    return;

                var skip = (Math.Max(1, PaginaActual) - 1) * Math.Max(1, TamanoPagina);
                var take = Math.Max(1, TamanoPagina);
                var search = string.IsNullOrWhiteSpace(FiltroTexto)
                    ? null
                    : FiltroTexto.Trim();

                var ct = NuevoToken();

                PagedResultReq pagina = await _servicio.ObtenerRequerimientosAsync(
                    AnioSeleccionado!.Id,
                    idAuditoria,
                    ActoSeleccionado!.ID_Acto,
                    idOficio,
                    idColabora,
                    skip,
                    take,
                    search,
                    ct);

                foreach (var r in pagina.Items)
                    Requerimientos.Add(r);

                TotalRequerimientos = pagina.Total;

                if (Requerimientos.Count == 1)
                {
                    RequerimientoSeleccionado = Requerimientos[0];
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar requerimientos:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private async Task CambiarPaginaAsync(int delta)
        {
            if (!EsActoNormal)
                return;

            var nueva = PaginaActual + delta;
            if (nueva < 1 || nueva > TotalPaginas)
                return;

            PaginaActual = nueva;
            await RefrescarRequerimientosAsync(resetPagina: false);
        }

        private async Task RefrescarAnexosCedulaAsync()
        {
            try
            {
                Cargando = true;

                AnexosCedula.Clear();
                AnexoSeleccionado = null;

                ResultadosCedula.Clear();
                ResultadoCedulaSeleccionado = null;

                if (AnioSeleccionado is null || EnteSeleccionado is null || ActoSeleccionado is null)
                    return;

                if (!TryGetIdAuditoria(EnteSeleccionado, out var idAuditoriaInt))
                    return;

                var idActo = ActoSeleccionado.ID_Acto;
                var idColabora = EnteSeleccionado.IdColabora;
                var ct = NuevoToken();

                var anexos = await _servicio.ObtenerAnexosCedulaAsync(
                    AnioSeleccionado!.Id,
                    idAuditoriaInt,
                    idColabora,
                    ct);

                if (anexos == null || anexos.Count == 0)
                {
                    MensajeEstado = "No se encontraron anexos para Cédula (o el servicio aún no expone el endpoint).";
                    return;
                }

                foreach (var a in anexos)
                    AnexosCedula.Add(a);

                AnexoSeleccionado = AnexosCedula.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar anexos de cédula:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private async Task RefrescarResultadosCedulaAsync()
        {
            try
            {
                Cargando = true;

                ResultadosCedula.Clear();
                ResultadoCedulaSeleccionado = null;

                if (!EsActoCedula)
                    return;

                if (AnioSeleccionado is null || EnteSeleccionado is null ||
                    ActoSeleccionado is null || AnexoSeleccionado is null)
                    return;

                if (!TryGetIdAuditoria(EnteSeleccionado, out var idAuditoriaInt))
                    return;

                var idActo = ActoSeleccionado.ID_Acto;
                var idColabora = EnteSeleccionado.IdColabora;
                var ct = NuevoToken();

                var idAnexo = TryGetIntFromDto(AnexoSeleccionado,
                    "ID_Anexo", "Id_Anexo", "AnexoId", "Anexo_Id", "Id");

                var idEnlace = EnteSeleccionado.IdColabora;
                var resultados = await _servicio.ObtenerResultadosCedulaAsync(
                    AnioSeleccionado!.Id,
                    idAuditoriaInt,
                    idEnlace,
                    ct);

                if (resultados == null || resultados.Count == 0)
                {
                    MensajeEstado = "No se encontraron resultados para el anexo seleccionado (o el servicio aún no expone el endpoint).";
                    return;
                }

                foreach (var r in resultados)
                    ResultadosCedula.Add(r);

                ResultadoCedulaSeleccionado = ResultadosCedula.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar resultados de cédula:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        /// <summary>
        /// Consulta y pinta los resultados del acto 21 para el oficio seleccionado.
        ///
        /// Si esta lista sale vacía, revisar primero en SQL:
        /// BUZ_Asignaciones_Resultados_Informe por ID_Movimiento + ID_Enlace.
        /// </summary>
        private async Task RefrescarResultadosInformeAsync()
        {
            try
            {
                Cargando = true;

                ResultadosInforme.Clear();
                ResultadoInformeSeleccionado = null;

                if (!EsActoInformeIndividual)
                    return;

                if (AnioSeleccionado is null || EnteSeleccionado is null ||
                    ActoSeleccionado is null || OficioSeleccionado is null)
                    return;

                if (!TryGetIdAuditoria(EnteSeleccionado, out var idAuditoriaInt))
                    return;

                var idOficio = OficioSeleccionado.IdOficio;
                var idColabora = EnteSeleccionado.IdColabora;
                var ct = NuevoToken();

                var resultados = await _servicio.ObtenerResultadosInformeIndividualAsync(
                    AnioSeleccionado!.Id,
                    idAuditoriaInt,
                    ActoSeleccionado.ID_Acto,
                    idOficio,
                    idColabora,
                    ct);

                if (resultados == null || resultados.Count == 0)
                {
                    MensajeEstado = "No se encontraron resultados asignados para el Informe Individual de Auditoría seleccionado.";
                    TotalRequerimientos = 0;
                    return;
                }

                foreach (var r in resultados)
                    ResultadosInforme.Add(r);

                TotalRequerimientos = ResultadosInforme.Count;
                ResultadoInformeSeleccionado = ResultadosInforme.FirstOrDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al cargar resultados del Informe Individual de Auditoría:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cargando = false;
            }
        }

        private static bool TryGetIdAuditoria(EnteDto ente, out int idAuditoriaInt)
        {
            idAuditoriaInt = 0;
            var idAuditoriaStr = ente.IdAuditoria ?? string.Empty;
            if (int.TryParse(idAuditoriaStr, out idAuditoriaInt))
                return true;

            MessageBox.Show(
                $"El IdAuditoria recibido ('{idAuditoriaStr}') no es numérico.",
                "Error en datos de auditoría",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }

        private static int TryGetIntFromDto(object dto, params string[] propNames)
        {
            foreach (var p in propNames)
            {
                try
                {
                    var pi = dto.GetType().GetProperty(p, BindingFlags.Public | BindingFlags.Instance);
                    if (pi == null) continue;

                    var val = pi.GetValue(dto);
                    if (val is int i) return i;

                    if (val is long l) return (int)l;

                    if (val is string s && int.TryParse(s, out var si))
                        return si;

                    if (val != null)
                    {
                        var converted = Convert.ChangeType(val, typeof(int));
                        if (converted is int ci) return ci;
                    }
                }
                catch
                {
                }
            }

            return 0; // si no existe, intentaremos invocar sin idAnexo en algunos overloads
        }

        private static async Task<List<T>?> TryInvokeListAsync<T>(
            object targetService,
            string[] methodNames,
            params object?[][] candidateArgs)
        {
            foreach (var name in methodNames)
            {
                var methods = targetService
                    .GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal))
                    .ToList();

                if (methods.Count == 0)
                    continue;

                foreach (var args in candidateArgs)
                {
                    foreach (var m in methods)
                    {
                        var ps = m.GetParameters();
                        if (ps.Length != args.Length)
                            continue;

                        if (!CanBind(ps, args, out var finalArgs))
                            continue;

                        try
                        {
                            var result = m.Invoke(targetService, finalArgs);

                            if (result is Task task)
                            {
                                await task.ConfigureAwait(false);

                                var taskType = task.GetType();
                                if (taskType.IsGenericType)
                                {
                                    var resProp = taskType.GetProperty("Result");
                                    var resVal = resProp?.GetValue(task);

                                    if (resVal is IEnumerable<T> enumerable)
                                        return enumerable.ToList();
                                }

                                continue;
                            }

                            if (result is IEnumerable<T> syncEnumerable)
                                return syncEnumerable.ToList();
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static bool CanBind(ParameterInfo[] ps, object?[] args, out object?[] finalArgs)
        {
            finalArgs = new object?[args.Length];

            for (int i = 0; i < ps.Length; i++)
            {
                var pType = ps[i].ParameterType;
                var a = args[i];

                if (a == null)
                {
                    if (pType.IsValueType && Nullable.GetUnderlyingType(pType) is null)
                        return false;

                    finalArgs[i] = null;
                    continue;
                }

                if (pType.IsInstanceOfType(a))
                {
                    finalArgs[i] = a;
                    continue;
                }

                try
                {
                    var converted = Convert.ChangeType(a, Nullable.GetUnderlyingType(pType) ?? pType);
                    finalArgs[i] = converted;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();

            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);

            foreach (var ch in formD)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString()
                     .Normalize(NormalizationForm.FormC)
                     .ToUpperInvariant();
        }

        /// <summary>
        /// Construye ContextoRequerimiento y abre la ventana de carga.
        ///
        /// Este método es la frontera entre la selección de catálogos y la transferencia física:
        /// todo dato que no se copie al contexto no llegará a ServicioCargaArchivos.
        /// </summary>
        private void ConfirmarSeleccion()
        {
            if (AnioSeleccionado is null || EnteSeleccionado is null || ActoSeleccionado is null)
            {
                MessageBox.Show(
                    "Debe seleccionar Año, Ente y Acto.",
                    "Validación",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (EsActoCedula)
            {
                if (AnexoSeleccionado is null || ResultadoCedulaSeleccionado is null)
                {
                    MessageBox.Show(
                        "Debe seleccionar un Anexo y un Resultado.",
                        "Validación",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            else if (EsActoInformeIndividual)
            {
                if (OficioSeleccionado is null || ResultadoInformeSeleccionado is null)
                {
                    MessageBox.Show(
                        "Debe seleccionar Año, Ente, Acto, Oficio y un Resultado.",
                        "Validación",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                if (OficioSeleccionado is null || RequerimientoSeleccionado is null)
                {
                    MessageBox.Show(
                        "Debe seleccionar Año, Ente, Acto, Oficio y un Requerimiento.",
                        "Validación",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            var contextoObj = Activator.CreateInstance(typeof(ContextoRequerimiento))
                             ?? throw new InvalidOperationException(
                                 "No se pudo crear instancia de ContextoRequerimiento.");

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "IdAnio", "AnioId", "Periodo", "Anio", "Year" },
                AnioSeleccionado.Id);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "NombreAnio", "AnioNombre", "DescripcionAnio" },
                AnioSeleccionado.Nombre);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "IdColabora", "Id_Colabora" },
                EnteSeleccionado.IdColabora);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "IdAuditoria", "ID_Auditoria" },
                EnteSeleccionado.IdAuditoria);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "EnteFiscalizado", "Ente", "NombreEnte" },
                EnteSeleccionado.EnteFiscalizado);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "IdActo", "ID_Acto" },
                ActoSeleccionado.ID_Acto);

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "NombreActo", "NomActo", "Nom_Acto" },
                ActoSeleccionado.Nom_Acto);

            if (EsActoNormal)
            {
                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdOficio", "IdMovimiento", "ID_Movimiento" },
                    OficioSeleccionado!.IdOficio);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "NumeroOficio", "Num_Oficio", "Oficio", "Numero" },
                    !string.IsNullOrWhiteSpace(OficioSeleccionado!.Numero)
                        ? OficioSeleccionado!.Numero
                        : OficioSeleccionado!.Descripcion);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdRequerimiento", "RequerimientoId", "Id" },
                    RequerimientoSeleccionado!.IdRequerimiento);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "DescripcionRequerimiento", "Requerimiento", "Texto" },
                    RequerimientoSeleccionado!.Descripcion);
            }
            else if (EsActoCedula)
            {
                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdOficio", "IdMovimiento", "ID_Movimiento" },
                    AnexoSeleccionado!.ID_Anexo);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "NumeroOficio", "Num_Oficio", "Oficio", "Numero" },
                    AnexoSeleccionado!.Anexo_Nombre);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdRequerimiento", "RequerimientoId", "Id" },
                    ResultadoCedulaSeleccionado!.ID);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "DescripcionRequerimiento", "Requerimiento", "Texto" },
                    ResultadoCedulaSeleccionado!.Resumen ?? string.Empty);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "ResultadoId", "IdResultado" },
                    ResultadoCedulaSeleccionado!.ID);
            }
            else if (EsActoInformeIndividual)
            {
                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdOficio", "IdMovimiento", "ID_Movimiento" },
                    OficioSeleccionado!.IdOficio);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "NumeroOficio", "Num_Oficio", "Oficio", "Numero" },
                    !string.IsNullOrWhiteSpace(OficioSeleccionado!.Numero)
                        ? OficioSeleccionado!.Numero
                        : OficioSeleccionado!.Descripcion);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "IdRequerimiento", "RequerimientoId", "Id" },
                    ResultadoInformeSeleccionado!.ID);

                // Informe Individual de Auditoría:
                // En la ventana de carga, el campo "Resultado seleccionado" debe mostrar
                // únicamente el No. de Resultado. El texto completo del resultado no se pierde:
                // permanece disponible en ResultadoInformeSeleccionado.Texto_Resultado para
                // mantenimiento, trazabilidad o futuras vistas, pero no debe mostrarse aquí
                // porque el comportamiento solicitado para este acto es diferente al de la
                // Cédula de Resultados Preliminares.
                var numeroResultadoInforme = ResultadoInformeSeleccionado!.Num_Resultado.HasValue
                    && ResultadoInformeSeleccionado!.Num_Resultado.Value > 0
                        ? ResultadoInformeSeleccionado!.Num_Resultado.Value.ToString(CultureInfo.InvariantCulture)
                        : ResultadoInformeSeleccionado!.ID.ToString(CultureInfo.InvariantCulture);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "DescripcionRequerimiento", "Requerimiento", "Texto" },
                    numeroResultadoInforme);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "ResultadoId", "IdResultado" },
                    ResultadoInformeSeleccionado!.ID);

                ReflexionUtil.SetIfExists(contextoObj,
                    new[] { "ApartadoRubro" },
                    ResultadoInformeSeleccionado!.Apartado_Rubro ?? string.Empty);
            }

            ReflexionUtil.SetIfExists(contextoObj,
                new[] { "Rfc", "RFC", "RfcUsuario", "RfcContribuyente" },
                _rfcUsuario);

            RequerimientoConfirmado?.Invoke(this, (ContextoRequerimiento)contextoObj);
        }

        private async Task VerArchivosLocalesAsync()
        {
            if (EsActoCedula)
            {
                if (ResultadoCedulaSeleccionado is null)
                {
                    MessageBox.Show(
                        "Primero seleccione un resultado de la lista.",
                        "Archivos registrados",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    var ct = NuevoToken();

                    var archivos = await _servicio.ObtenerArchivosLocalesPorResultadoAsync(
                        ResultadoCedulaSeleccionado.ID,
                        EnteSeleccionado!.IdColabora,
                        AnexoSeleccionado!.ID_Anexo,
                        ct);

                    if (archivos is null || archivos.Count == 0)
                    {
                        MessageBox.Show(
                            "No se encontraron archivos registrados para este resultado.",
                            "Archivos registrados para el resultado seleccionado",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var owner = Application.Current?.Windows.Count > 0
                        ? Application.Current.Windows[0]
                        : null;

                    var ventana = new VentanaArchivosLocales(
                        archivos,
                        0,
                        EnteSeleccionado.IdColabora,
                        _rfcUsuario,
                        true,
                        ResultadoCedulaSeleccionado.ID,
                        AnexoSeleccionado.ID_Anexo)
                                        {
                                            Owner = owner
                                        };

                    ventana.ShowDialog();

                    await RefrescarResultadosCedulaAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error al obtener archivos del resultado:\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                return;
            }

            if (EsActoInformeIndividual)
            {
                if (ResultadoInformeSeleccionado is null)
                {
                    MessageBox.Show(
                        "Primero seleccione un resultado de la lista.",
                        "Archivos registrados",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    var ct = NuevoToken();

                    var archivos = await _servicio.ObtenerArchivosLocalesPorResultadoAsync(
                        ResultadoInformeSeleccionado.ID,
                        ResultadoInformeSeleccionado.ID_Colabora,
                        ResultadoInformeSeleccionado.ID_Oficio,
                        ct);

                    if (archivos is null || archivos.Count == 0)
                    {
                        MessageBox.Show(
                            "No se encontraron archivos registrados para este resultado.",
                            "Archivos registrados para el resultado seleccionado",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var owner = Application.Current?.Windows.Count > 0
                        ? Application.Current.Windows[0]
                        : null;

                    var ventana = new VentanaArchivosLocales(
                        archivos,
                        0,
                        ResultadoInformeSeleccionado.ID_Colabora,
                        _rfcUsuario,
                        true,
                        ResultadoInformeSeleccionado.ID,
                        ResultadoInformeSeleccionado.ID_Oficio)
                    {
                        Owner = owner
                    };

                    ventana.ShowDialog();

                    await RefrescarResultadosInformeAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error al obtener archivos del resultado:\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                return;
            }

            if (RequerimientoSeleccionado is null)
            {
                MessageBox.Show(
                    "Primero seleccione un requerimiento de la lista.",
                    "Archivos locales",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (EnteSeleccionado is null)
            {
                MessageBox.Show(
                    "Primero seleccione el ente / auditoría correspondiente.",
                    "Archivos locales",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                var ct = NuevoToken();

                var archivos = await _servicio.ObtenerArchivosLocalesAsync(
                    _rfcUsuario,
                    RequerimientoSeleccionado.IdRequerimiento,
                    EnteSeleccionado.IdColabora,
                    ct);

                if (archivos is null || archivos.Count == 0)
                {
                    MessageBox.Show(
                        "No se encontraron archivos locales registrados para este requerimiento.",
                        "Archivos locales",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var owner = Application.Current?.Windows.Count > 0
                    ? Application.Current.Windows[0]
                    : null;

                var ventana = new VentanaArchivosLocales(
                    archivos,
                    RequerimientoSeleccionado.IdRequerimiento,
                    EnteSeleccionado.IdColabora,
                    _rfcUsuario)
                {
                    Owner = owner
                };

                ventana.ShowDialog();

                await RefrescarRequerimientosAsync(resetPagina: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error al obtener archivos locales:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

    }

    internal static class ReflexionUtil
    {
        public static bool SetIfExists(object target, string[] names, object? value)
        {
            if (target == null || names == null || names.Length == 0)
                return false;

            var t = target.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name);
                if (p != null && p.CanWrite)
                {
                    try
                    {
                        var v = value;
                        var tgt = p.PropertyType;

                        if (v != null && !tgt.IsAssignableFrom(v.GetType()))
                        {
                            if (tgt.IsEnum && v is string sv)
                                v = Enum.Parse(tgt, sv, true);
                            else
                                v = Convert.ChangeType(v, tgt);
                        }

                        p.SetValue(target, v);
                        return true;
                    }
                    catch
                    {
                    }
                }
            }
            return false;
        }
    }
}
