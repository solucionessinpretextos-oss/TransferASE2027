// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/ContextoRequerimiento.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Notas específicas:
// - Este objeto viaja de la pantalla de selección a la pantalla de carga; si falta un ID aquí, la catalogación queda incompleta.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.


// ============================================================================
//  ContextoRequerimiento
// ============================================================================
//  Este modelo es la "bolsa de contexto" que viaja desde la pantalla de selección
//  hasta la ventana de carga de documentos.
//
//  Contiene la ruta funcional completa elegida por el usuario:
//      Año -> Ente -> Auditoría -> Acto -> Oficio -> Requerimiento/Resultado.
//
//  Por compatibilidad con distintos DTOs, mantiene varios alias de propiedades
//  (por ejemplo IdAuditoria / ID_Auditoria). Eso permite que pantallas anteriores,
//  bindings XAML y servicios nuevos lean el mismo dato sin reescribir todo el cliente.
//
//  Regla de negocio:
//  - Para actos normales se usa Requerimiento.
//  - Para acto 16 y acto 21 se usa ResultadoId.
//  - Para acto 21 ResultadoId debe ser BUZ_Resultados_CedulaEvaluacion.ID.
// ============================================================================
namespace TransferClienteASE.Modelos
{
    /// <summary>
    /// Contexto seleccionado por el usuario antes de abrir la ventana de carga.
    /// Es el objeto que permite que ServicioCargaArchivos sepa contra qué entidad de negocio
    /// debe registrar cada archivo.
    /// </summary>
    public sealed class ContextoRequerimiento
    {

        public AnioPaavi? Anio { get; set; }

        public EnteFiscalizado? Ente { get; set; }

        public Auditoria? Auditoria { get; set; }

        public ActoFiscalizacion? Acto { get; set; }

        public OficioSolicitud? Oficio { get; set; }

        public Requerimiento? Requerimiento { get; set; }

        public int IdAnio
        {
            get => Anio?.Id ?? 0;
            set
            {
                if (value <= 0) return;
                Anio ??= new AnioPaavi();
                Anio.Id = value;
                if (Anio.Anio == 0)
                {
                    Anio.Anio = value;
                }
            }
        }

        public int AnioId
        {
            get => IdAnio;
            set => IdAnio = value;
        }

        public int Periodo
        {
            get => IdAnio;
            set => IdAnio = value;
        }

        private string? _nombreAnio;
        public string? NombreAnio
        {
            get => _nombreAnio;
            set => _nombreAnio = value;
        }

        public string? AnioNombre
        {
            get => NombreAnio;
            set => NombreAnio = value;
        }

        public string? DescripcionAnio
        {
            get => NombreAnio;
            set => NombreAnio = value;
        }

        public int IdColabora
        {
            get => Ente?.Id ?? 0;
            set
            {
                if (value <= 0) return;
                Ente ??= new EnteFiscalizado();
                Ente.Id = value;
            }
        }

        public int Id_Colabora
        {
            get => IdColabora;
            set => IdColabora = value;
        }

        private string? _enteNombre;
        public string EnteFiscalizado
        {
            get => _enteNombre ?? Ente?.Nombre ?? string.Empty;
            set
            {
                _enteNombre = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Ente ??= new EnteFiscalizado();
                    Ente.Nombre = value;
                }
            }
        }

        public string NombreEnte
        {
            get => EnteFiscalizado;
            set => EnteFiscalizado = value;
        }

        private string? _idAuditoriaTexto;
        public string IdAuditoria
        {
            get => _idAuditoriaTexto ?? (Auditoria?.Id > 0 ? Auditoria.Id.ToString() : string.Empty);
            set
            {
                _idAuditoriaTexto = value;
                if (int.TryParse(value, out var id) && id > 0)
                {
                    Auditoria ??= new Auditoria();
                    Auditoria.Id = id;
                }
            }
        }

        public string ID_Auditoria
        {
            get => IdAuditoria;
            set => IdAuditoria = value;
        }

        public int IdActo
        {
            get => Acto?.Id ?? 0;
            set
            {
                if (value <= 0) return;
                Acto ??= new ActoFiscalizacion();
                Acto.Id = value;
            }
        }

        public int ID_Acto
        {
            get => IdActo;
            set => IdActo = value;
        }

        private string? _nombreActo;
        public string NombreActo
        {
            get => _nombreActo ?? Acto?.Descripcion ?? string.Empty;
            set
            {
                _nombreActo = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Acto ??= new ActoFiscalizacion();
                    Acto.Descripcion = value;
                }
            }
        }

        public string NomActo
        {
            get => NombreActo;
            set => NombreActo = value;
        }

        public string Nom_Acto
        {
            get => NombreActo;
            set => NombreActo = value;
        }

        public int IdOficio
        {
            get => Oficio?.Id ?? 0;
            set
            {
                if (value <= 0) return;
                Oficio ??= new OficioSolicitud();
                Oficio.Id = value;
            }
        }

        public int IdMovimiento
        {
            get => IdOficio;
            set => IdOficio = value;
        }

        public int ID_Movimiento
        {
            get => IdOficio;
            set => IdOficio = value;
        }

        private string? _numeroOficio;
        public string NumeroOficio
        {
            get => _numeroOficio ?? Oficio?.Folio ?? string.Empty;
            set
            {
                _numeroOficio = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Oficio ??= new OficioSolicitud();
                    Oficio.Folio = value;
                }
            }
        }

        public string Num_Oficio
        {
            get => NumeroOficio;
            set => NumeroOficio = value;
        }

        public string Numero
        {
            get => NumeroOficio;
            set => NumeroOficio = value;
        }

        public int IdRequerimiento
        {
            get => Requerimiento?.Id ?? 0;
            set
            {
                if (value <= 0) return;
                Requerimiento ??= new Requerimiento();
                Requerimiento.Id = value;
            }
        }

        public int RequerimientoId
        {
            get => IdRequerimiento;
            set => IdRequerimiento = value;
        }

        public int Id
        {
            get => IdRequerimiento;
            set => IdRequerimiento = value;
        }

        private string? _descripcionRequerimiento;
        public string DescripcionRequerimiento
        {
            get => _descripcionRequerimiento ?? Requerimiento?.Descripcion ?? string.Empty;
            set
            {
                _descripcionRequerimiento = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Requerimiento ??= new Requerimiento();
                    Requerimiento.Descripcion = value;
                }
            }
        }

        public string Texto
        {
            get => DescripcionRequerimiento;
            set => DescripcionRequerimiento = value;
        }

        private int _resultadoId;
        public int ResultadoId
        {
            get => _resultadoId;
            set => _resultadoId = value;
        }

        public int IdResultado
        {
            get => ResultadoId;
            set => ResultadoId = value;
        }

        public string Rfc { get; set; } = string.Empty;

        public string RFC
        {
            get => Rfc;
            set => Rfc = value;
        }

        public string RfcUsuario
        {
            get => Rfc;
            set => Rfc = value;
        }

        public string RfcContribuyente
        {
            get => Rfc;
            set => Rfc = value;
        }

        public string ApartadoRubro { get; set; } = string.Empty;

        // Propiedad calculada para mostrar el apartado/rubro en la UI.
        // Es de solo lectura; por eso cualquier binding XAML hacia TextBox.Text debe usar
        // Mode=OneWay o debe mostrarse en TextBlock. Si WPF intenta escribir aquí, lanzará
        // InvalidOperationException por no existir setter.
        public string ApartadoRubroDescripcion
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ApartadoRubro))
                    return ApartadoRubro;

                if (!string.IsNullOrWhiteSpace(NumeroOficio))
                    return string.IsNullOrWhiteSpace(NombreActo)
                        ? NumeroOficio
                        : $"{NombreActo} / {NumeroOficio}";

                return NombreActo;
            }
        }
    }
}
