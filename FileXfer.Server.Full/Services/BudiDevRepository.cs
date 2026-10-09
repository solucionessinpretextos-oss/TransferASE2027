// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/BudiDevRepository.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Notas específicas:
// - Regla Delta: en acto 21, ResultadoId siempre debe ser BUZ_Resultados_CedulaEvaluacion.ID, nunca BUZ_Resultados_Cedulas.ID.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using Dapper;
using FileXfer.Server.Dtos.Combos;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


// ============================================================================
//  DOCUMENTACIÓN INTERNA — BudiDevRepository
// ============================================================================
//  Esta clase es la frontera principal entre FileXfer.Server y la base BuDi_dev.
//
//  Responsabilidad general:
//  - Leer catálogos del Buzón Digital para poblar combos del cliente Transfer ASE.
//  - Resolver la lógica especial de resultados por acto:
//      * Actos normales: trabajan con requerimientos.
//      * Acto 16 Cédula de Resultados Preliminares: trabaja con BUZ_Resultados_Cedulas.
//      * Acto 21 Informe Individual de Auditoría: trabaja con
//        BUZ_Asignaciones_Resultados_Informe como tabla base y usa
//        BUZ_Resultados_CedulaEvaluacion solo como tabla descriptiva.
//  - Registrar, consultar y dar de baja archivos locales en Transfer_ArchivosLocales.
//
//  Regla de negocio crítica para acto 21:
//  ------------------------------------------------------------
//  BUZ_Asignaciones_Resultados_Informe es la tabla que manda.
//  Cada fila indica qué resultado de Informe Individual fue asignado a un enlace
//  en un movimiento/oficio específico.
//
//  BUZ_Resultados_CedulaEvaluacion describe el resultado del acto 21.
//  Su columna ID es el identificador que debe guardarse en Transfer_ArchivosLocales.ResultadoId.
//
//  BUZ_Resultados_Cedulas solo se usa como referencia histórica cuando
//  BUZ_Resultados_CedulaEvaluacion.ID_Resultado no es NULL. Esa relación permite saber
//  que el resultado del acto 21 proviene del acto 16, pero NO significa que los archivos
//  del acto 21 deban guardarse contra BUZ_Resultados_Cedulas.ID.
//
//  Para evitar mezclar evidencias entre actos, el conteo, consulta y baja lógica de
//  documentos por resultado deben filtrar como mínimo por:
//      ResultadoId + ColaboraId + MovimientoId
//
//  Si un programador modifica este repositorio, debe verificar que esa regla siga intacta.
// ============================================================================
namespace FileXfer.Server.Services
{
    public sealed class BudiDevRepository : IBudiDevRepository
    {
        private readonly string _connectionString;
        private readonly string _storageBasePath;
        private readonly IPathProtector _pathProtector;

        public BudiDevRepository(
            IConfiguration configuration,
            StorageOptions storageOptions,
            IPathProtector pathProtector)
        {
            _connectionString =
                configuration.GetConnectionString("BudiDev")
                ?? throw new InvalidOperationException(
                    "Falta la cadena de conexión 'BudiDev' en appsettings.json.");

            _storageBasePath = storageOptions?.BasePath?.Trim()
                ?? throw new InvalidOperationException(
                    "Falta Storage:BasePath en la configuración del servicio.");

            _pathProtector = pathProtector ?? throw new ArgumentNullException(nameof(pathProtector));

            _storageBasePath = _storageBasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // Crea una conexión nueva por operación.
        // No se comparte SqlConnection entre métodos porque las consultas son asíncronas
        // y cada operación debe controlar su propio ciclo de vida, transacción y cancelación.
        private SqlConnection Conn() => new SqlConnection(_connectionString);

        /// <summary>
        /// Convierte la ruta persistida en Transfer_ArchivosLocales a una ruta física absoluta.
        ///
        /// La tabla guarda rutas relativas cuando el archivo queda dentro del storage base del
        /// servidor. Si en algún caso histórico la ruta ya viene absoluta, se conserva.
        /// Esta normalización es necesaria porque la ruta almacenada se encripta antes de entrar
        /// a BuDi y se desencripta al consultar/borrar documentos.
        /// </summary>
        private string ConstruirRutaAbsoluta(string rutaArchivoFisico)
        {
            if (string.IsNullOrWhiteSpace(rutaArchivoFisico))
                throw new ArgumentException("La ruta del archivo no puede ser vacía.", nameof(rutaArchivoFisico));

            string candidate;
            if (Path.IsPathRooted(rutaArchivoFisico))
            {
                candidate = rutaArchivoFisico;
            }
            else
            {
                var segmentos = rutaArchivoFisico
                    .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (segmentos.Length == 0)
                    throw new InvalidOperationException("No se pudieron obtener segmentos válidos de la ruta del archivo.");

                candidate = Path.Combine(new[] { _storageBasePath }.Concat(segmentos).ToArray());
            }

            var root = Path.GetFullPath(_storageBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(candidate);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La ruta resuelta queda fuera del almacenamiento autorizado.");

            return fullPath;
        }

        /// <summary>
        /// Encripta la ruta física que será persistida en Transfer_ArchivosLocales.
        ///
        /// Importante: el archivo real queda en disco o recurso compartido; la base solo conserva
        /// una versión protegida de la ruta. Por eso cualquier consulta posterior debe desencriptar
        /// Ruta_ARchivo_Fisico antes de intentar abrir o eliminar el archivo físico.
        /// </summary>
        private string ObtenerRutaEncriptada(string rutaArchivoFisico)
        {
            var rutaCompleta = ConstruirRutaAbsoluta(rutaArchivoFisico);
            return _pathProtector.Protect(rutaCompleta ?? string.Empty);
        }

        public Task<IReadOnlyList<AnioDto>> GetYearsAsync(int cuantos, CancellationToken ct)
        {
            var lista = new List<AnioDto>();

            var year = DateTime.UtcNow.Year;
            for (int i = 0; i < cuantos; i++)
            {
                var y = year - i;

                lista.Add(new AnioDto
                {
                    Id = y,
                    Nombre = y.ToString()
                });
            }

            return Task.FromResult((IReadOnlyList<AnioDto>)lista);
        }

        public async Task<IReadOnlyList<EnteDto>> GetEntesAsync(
            string rfc,
            int periodo,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rfc))
                throw new ArgumentException("RFC no puede ser vacío.", nameof(rfc));

            await using var cn = Conn();

            var p = new DynamicParameters();
            p.Add("@RFC", rfc.Trim().ToUpperInvariant());
            p.Add("@Periodo", periodo);

            var data = await cn.QueryAsync<DbEnteAuditoria>(
                new CommandDefinition(
                    commandText: "PA_Consulta_AuditoriasDigitalesByRFC",
                    parameters: p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct));

            var list = data
                .Select(x => new EnteDto
                {
                    IdColabora = x.Id_Colabora,
                    IdAuditoria = x.ID_Auditoria.ToString(CultureInfo.InvariantCulture),
                    EnteFiscalizado = x.Ente_Fiscalizado ?? string.Empty,
                    Auditoria = x.Auditoria ?? string.Empty,
                    Nombre = string.IsNullOrWhiteSpace(x.Auditoria)
                        ? (x.Ente_Fiscalizado ?? string.Empty)
                        : x.Auditoria
                })
                .ToList()
                .AsReadOnly();

            return list;
        }

        public async Task<bool> IsUploadContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int? idRequerimiento,
            int? idResultado,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rfc) || periodo <= 0 || idEnte <= 0 ||
                idAuditoria <= 0 || idActo <= 0 || idOficio <= 0)
            {
                return false;
            }

            if ((idRequerimiento.GetValueOrDefault() > 0) == (idResultado.GetValueOrDefault() > 0))
                return false;

            if (!await IsOficioContextAuthorizedAsync(
                    rfc,
                    periodo,
                    idEnte,
                    idAuditoria,
                    idActo,
                    idOficio,
                    ct))
                return false;

            if (idResultado.GetValueOrDefault() > 0)
            {
                var resultadoId = idResultado!.Value;
                if (idActo == 21)
                {
                    var informe = await GetResultadosInformeIndividualAsync(idAuditoria, idOficio, idEnte, ct);
                    return informe.Any(resultado => resultado.IdResultado == resultadoId);
                }

                if (idActo == 16)
                {
                    var cedulas = await GetResultadosCedulasPorAuditoriaAsync(idAuditoria, idEnte, ct);
                    return cedulas.Any(resultado => resultado.IdResultado == resultadoId);
                }

                return false;
            }

            // El procedimiento ya restringe por oficio y enlace. Pedir un bloque amplio evita
            // confiar en un identificador suministrado por el cliente sin duplicar la lógica SQL.
            var requerimientos = await GetRequerimientosAsync(
                idOficio,
                idEnte,
                skip: 0,
                take: 10000,
                search: null,
                ct);

            return requerimientos.Items.Any(requerimiento =>
                requerimiento.IdRequerimiento == idRequerimiento!.Value);
        }

        public async Task<bool> IsAuditContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rfc) || periodo <= 0 || idEnte <= 0 || idAuditoria <= 0)
                return false;

            var entes = await GetEntesAsync(rfc, periodo, ct);
            return entes.Any(ente =>
                ente.IdColabora == idEnte &&
                int.TryParse(
                    ente.IdAuditoria,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var auditoria) &&
                auditoria == idAuditoria);
        }

        public async Task<bool> IsOficioContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            CancellationToken ct)
        {
            if (idActo <= 0 || idOficio <= 0 ||
                !await IsAuditContextAuthorizedAsync(rfc, periodo, idEnte, idAuditoria, ct))
            {
                return false;
            }

            var oficios = await GetOficiosAsync(idAuditoria, idActo, idEnte, ct);
            return oficios.Any(oficio => oficio.IdOficio == idOficio);
        }

        public async Task<IReadOnlyList<ActoDto>> GetActosAsync(CancellationToken ct)
        {
            using var cn = Conn();

            var data = await cn.QueryAsync<ActoDto>(
                new CommandDefinition(
                    "PA_Consulta_Actos",
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct));

            var list = data
                .Select(x => new ActoDto
                {
                    ID_Acto = x.ID_Acto,
                    Nom_Acto = x.Nom_Acto ?? string.Empty
                })
                .ToList()
                .AsReadOnly();

            return list;
        }

        /// <summary>
        /// Devuelve los oficios disponibles para la auditoría, acto y enlace seleccionados.
        ///
        /// El método tiene dos rutas:
        /// 1. Acto 21 - Informe Individual de Auditoría:
        ///    Se consulta directamente BUZ_Movimientos unido con
        ///    BUZ_Asignaciones_Resultados_Informe. Esto es indispensable porque el acto 21
        ///    asigna resultados por enlace desde esa tabla y no desde la asignación normal de
        ///    requerimientos.
        /// 2. Resto de actos:
        ///    Se delega al procedimiento histórico PA_Consulta_OficiosDigitalesByActoColabora.
        ///
        /// Si el combo de oficio aparece vacío en el cliente, validar primero con SQL si existen
        /// filas en BUZ_Asignaciones_Resultados_Informe para ID_Movimiento + ID_Enlace.
        /// </summary>
        public async Task<IReadOnlyList<OficioDto>> GetOficiosAsync(
            int idAuditoria,
            int idActo,
            int idColabora,
            CancellationToken ct)
        {
            using var cn = Conn();

            // La detección por nombre evita quemar el número de acto en código.
            // En pruebas el acto 21 corresponde a Informe Individual de Auditoría, pero esta
            // comparación permite mantener la lógica si el catálogo cambia de ID entre ambientes.
            const string sql = @"
                DECLARE @EsInformeIndividual bit = 0;

                SELECT @EsInformeIndividual =
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM [dbo].[BUZ_Cat_Actos]
                            WHERE [ID_Acto] = @IdActo
                              AND UPPER([Nom_Acto]) LIKE '%INFORME INDIVIDUAL%'
                        ) THEN 1
                        ELSE 0
                    END;

                IF (@EsInformeIndividual = 1)
                BEGIN
                    SELECT DISTINCT
                        mov.[ID_Movimiento],
                        COALESCE(NULLIF(mov.[Num_Oficio], ''), mov.[Oficio_Nombre], CONVERT(varchar(20), mov.[ID_Movimiento])) AS Oficio_Nombre
                    FROM [dbo].[BUZ_Movimientos] mov
                    INNER JOIN [dbo].[BUZ_Asignaciones_Resultados_Informe] ari
                        ON ari.[ID_Movimiento] = mov.[ID_Movimiento]
                    INNER JOIN [dbo].[BUZ_Resultados_CedulaEvaluacion] r
                        ON r.[ID] = ari.[ID_Resultado_Informe]
                    WHERE mov.[ID_Auditoria] = @IdAuditoria
                      AND mov.[ID_Acto] = @IdActo
                      AND ari.[ID_Enlace] = @IdColabora
                    ORDER BY Oficio_Nombre;
                END
                ELSE
                BEGIN
                    EXEC [dbo].[PA_Consulta_OficiosDigitalesByActoColabora]
                        @IdAuditoria = @IdAuditoria,
                        @IdActo = @IdActo,
                        @IdColabora = @IdColabora;
                END;";

            var data = await cn.QueryAsync<OficioDbRow>(
                new CommandDefinition(
                    sql,
                    new { IdAuditoria = idAuditoria, IdActo = idActo, IdColabora = idColabora },
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            var list = data
                .Select(x => new OficioDto
                {
                    IdOficio = x.ID_Movimiento,
                    Numero = x.Oficio_Nombre ?? string.Empty,
                    Descripcion = x.Oficio_Nombre ?? string.Empty
                })
                .ToList()
                .AsReadOnly();

            return list;
        }

        public async Task<PagedResult<RequerimientoDto>> GetRequerimientosAsync(
            int idOficio,
            int idColabora,
            int skip,
            int take,
            string? search,
            CancellationToken ct)
        {
            using var cn = Conn();

            var p = new DynamicParameters();
            p.Add("@IdOficio", idOficio);
            p.Add("@IdColabora", idColabora);
            p.Add("@Skip", skip);
            p.Add("@Take", take);
            p.Add("@Search", search);

            using var grid = await cn.QueryMultipleAsync(new CommandDefinition(
                "PA_Consulta_RequerimientosByIdOficioV2",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            var items = (await grid.ReadAsync<RequerimientoDto>()).ToList();
            var total = await grid.ReadFirstOrDefaultAsync<int>();

            return new PagedResult<RequerimientoDto>(items, total);
        }

        public async Task<IReadOnlyList<AnexoCedulaDto>> GetAnexosCedulasPorAuditoriaAsync(
            int idAuditoria,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sql = @"
                SELECT
                    BEA.ID_Anexo                     AS IdAnexo,
                    BEA.Anexo_Nombre                 AS AnexoNombre,
                    ISNULL(BEA.Num_Resultados_Cedula, 0) AS NumResultadosCedula
                FROM [dbo].[BUZ_Eventos_Anexos] BEA
                INNER JOIN [dbo].[BUZ_Eventos_Movimientos] BEM
                    ON BEA.ID_Evento = BEM.ID
                WHERE BEM.ID_Auditoria = @IdAuditoria
                  AND BEA.Cedula = 1;";

            var data = await cn.QueryAsync<AnexoCedulaDto>(
                new CommandDefinition(
                    sql,
                    new { IdAuditoria = idAuditoria },
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            return data
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Obtiene resultados del acto 16 Cédula de Resultados Preliminares.
        ///
        /// En este acto el resultado base sí es BUZ_Resultados_Cedulas.ID. Los archivos cargados
        /// para este flujo se catalogan por ResultadoId usando ese identificador. Esta regla NO
        /// debe trasladarse automáticamente al acto 21, porque en Informe Individual el resultado
        /// operativo es BUZ_Resultados_CedulaEvaluacion.ID.
        /// </summary>
        public async Task<IReadOnlyList<ResultadoCedulaDto>> GetResultadosCedulasPorAuditoriaAsync(
            int idAuditoria,
            int idEnlace,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sqlResultados = @"
                SELECT
                    rc.ID                       AS IdResultado,
                    rc.ID_Anexo                 AS IdAnexo,
                    ISNULL(rc.Num_Resultado, 0) AS NumResultado,
                    rc.Resumen                  AS Resumen,
                    0                           AS NumeroEvidencias
                FROM [dbo].[BUZ_Resultados_Cedulas] rc
                    inner join BUZ_Asignaciones_Resumen_Resultados arr
                    on rc.id=arr.id_cedula_resultado
                    WHERE
                    rc.ID_Auditoria = @IdAuditoria
                    AND arr.ID_Enlace   = @IdEnlace
                    ORDER BY rc.Num_Resultado;
                ";

            var resultados = (await cn.QueryAsync<ResultadoCedulaDto>(
                    new CommandDefinition(
                        sqlResultados,
                        new { idAuditoria, idEnlace },
                        commandType: CommandType.Text,
                        cancellationToken: ct)))
                .ToList();

            if (resultados.Count == 0)
            {
                return Array.Empty<ResultadoCedulaDto>();
            }

            var ids = resultados
                .Select(r => r.IdResultado)
                .Distinct()
                .ToArray();

            const string sqlEvidencias = @"
                SELECT
                    COALESCE([ResultadoId], [RequerimientoId]) AS IdResultado,
                    COUNT(1)                                  AS NumeroEvidencias
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE [Estatus] = 'A'
                  AND [ColaboraId] = @IdEnlace
                  AND (
                        [ResultadoId] IN @Ids
                        OR ([ResultadoId] IS NULL AND [RequerimientoId] IN @Ids)
                      )
                GROUP BY COALESCE([ResultadoId], [RequerimientoId]);";

            var evidencias = await cn.QueryAsync<_ResultadoEvidenciaRow>(
                new CommandDefinition(
                    sqlEvidencias,
                    new { Ids = ids, IdEnlace = idEnlace },
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            var mapaEvidencias = evidencias.ToDictionary(
                x => x.IdResultado,
                x => x.NumeroEvidencias);

            foreach (var resultado in resultados)
            {
                if (mapaEvidencias.TryGetValue(resultado.IdResultado, out var num))
                {
                    resultado.NumeroEvidencias = num;
                }
                else
                {
                    resultado.NumeroEvidencias = 0;
                }
            }

            return resultados.AsReadOnly();
        }

        /// <summary>
        /// Obtiene resultados del acto 21 Informe Individual de Auditoría para el oficio seleccionado.
        ///
        /// Tabla base: BUZ_Asignaciones_Resultados_Informe.
        ///     - ID_Movimiento identifica el oficio/movimiento.
        ///     - ID_Enlace identifica el enlace/colaborador autenticado.
        ///     - ID_Resultado_Informe apunta a BUZ_Resultados_CedulaEvaluacion.ID.
        ///
        /// Tabla descriptiva: BUZ_Resultados_CedulaEvaluacion.
        ///     - ID es el ResultadoId que se debe guardar en Transfer_ArchivosLocales.
        ///     - ID_Resultado puede apuntar al resultado original del acto 16 o ser NULL cuando
        ///       el resultado fue capturado manualmente en el acto 21.
        ///
        /// Tabla de referencia histórica: BUZ_Resultados_Cedulas.
        ///     - Solo se usa para completar número, resumen o rubro cuando el resultado del acto 21
        ///       proviene del acto 16.
        ///
        /// Esta separación evita que la evidencia del Informe Individual quede asociada al acto
        /// equivocado.
        /// </summary>
        public async Task<IReadOnlyList<ResultadoInformeDto>> GetResultadosInformeIndividualAsync(
            int idAuditoria,
            int idOficio,
            int idColabora,
            CancellationToken ct)
        {
            await using var cn = Conn();

            // ResultadoId se toma de r.ID, no de r.ID_Resultado.
            // r.ID_Resultado únicamente documenta si el resultado proviene del acto 16.
            const string sqlResultados = @"
                SELECT
                    r.[ID] AS IdResultado,
                    ari.[ID_Movimiento] AS IdOficio,
                    mov.[ID_Auditoria] AS IdAuditoria,
                    ari.[ID_Enlace] AS IdColabora,
                    ISNULL(COALESCE(r.[Num_Resultado], rc.[Num_Resultado]), 0) AS NumResultado,
                    ISNULL(CONVERT(nvarchar(max), COALESCE(r.[Resumen], rc.[Resumen])), N'') AS TextoResultado,
                    COALESCE(rubros.[Nom_Rubro], N'') AS ApartadoRubro,
                    0 AS NumeroEvidencias,
                    r.[ID_Resultado] AS IdResultadoActo16,
                    CASE WHEN r.[ID_Resultado] IS NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS EsCapturadoManual,
                    CASE WHEN r.[ID_Resultado] IS NULL THEN N'CAPTURADO_ACTO_21' ELSE N'PROVIENE_ACTO_16' END AS OrigenResultado
                FROM [dbo].[BUZ_Asignaciones_Resultados_Informe] ari
                INNER JOIN [dbo].[BUZ_Resultados_CedulaEvaluacion] r
                    ON r.[ID] = ari.[ID_Resultado_Informe]
                LEFT JOIN [dbo].[BUZ_Resultados_Cedulas] rc
                    ON rc.[ID] = r.[ID_Resultado]
                LEFT JOIN [dbo].[BUZ_Cat_Rubros] rubros
                    ON rubros.[ID_Rubro] = COALESCE(r.[ID_Rubro], rc.[ID_Rubro])
                INNER JOIN [dbo].[BUZ_Movimientos] mov
                    ON mov.[ID_Movimiento] = ari.[ID_Movimiento]
                WHERE ari.[ID_Movimiento] = @IdOficio
                  AND ari.[ID_Enlace] = @IdColabora
                  AND mov.[ID_Auditoria] = @IdAuditoria
                ORDER BY ISNULL(COALESCE(r.[Num_Resultado], rc.[Num_Resultado]), 0), r.[ID];";

            var resultados = (await cn.QueryAsync<ResultadoInformeDto>(
                    new CommandDefinition(
                        sqlResultados,
                        new { IdAuditoria = idAuditoria, IdOficio = idOficio, IdColabora = idColabora },
                        commandType: CommandType.Text,
                        cancellationToken: ct)))
                .Where(r => r.IdResultado > 0)
                .GroupBy(r => r.IdResultado)
                .Select(g => g.First())
                .OrderBy(r => r.NumResultado)
                .ThenBy(r => r.IdResultado)
                .ToList();

            if (resultados.Count == 0)
            {
                return Array.Empty<ResultadoInformeDto>();
            }

            var ids = resultados.Select(r => r.IdResultado).Distinct().ToArray();

            // Para acto 21 el conteo de documentos debe incluir MovimientoId.
            // Si solo se contara por ResultadoId, podrían mezclarse evidencias de otro oficio,
            // otro enlace o incluso de un acto distinto con IDs numéricos coincidentes.
            const string sqlEvidencias = @"
                SELECT
                    [ResultadoId] AS IdResultado,
                    COUNT(1) AS NumeroEvidencias
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE [Estatus] = 'A'
                  AND [ResultadoId] IN @Ids
                  AND [ColaboraId] = @IdColabora
                  AND [MovimientoId] = @IdOficio
                GROUP BY [ResultadoId];";

            var evidencias = await cn.QueryAsync<_ResultadoEvidenciaRow>(
                new CommandDefinition(
                    sqlEvidencias,
                    new { Ids = ids, IdColabora = idColabora, IdOficio = idOficio },
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            var mapaEvidencias = evidencias.ToDictionary(
                x => x.IdResultado,
                x => x.NumeroEvidencias);

            foreach (var resultado in resultados)
            {
                if (mapaEvidencias.TryGetValue(resultado.IdResultado, out var total))
                    resultado.NumeroEvidencias = total;
            }

            return resultados.AsReadOnly();
        }

        public async Task RegistrarArchivoLocalAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            CancellationToken ct)
        {
            await RegistrarArchivoLocalConUploadAsync(
                rfcOrigen,
                requerimientoId,
                colaboraId,
                nombreArchivo,
                extension,
                rutaArchivoFisico,
                tamano,
                hash8,
                idUpload: null,
                ct);
        }

        /// <summary>
        /// Registra en Transfer_ArchivosLocales un archivo asociado a un requerimiento normal.
        ///
        /// Esta ruta es para flujos tradicionales donde el documento responde a RequerimientoId.
        /// No debe usarse para resultados de Cédula o Informe Individual; esos flujos usan
        /// RegistrarArchivoLocalPorResultadoConUploadAsync.
        /// </summary>
        public async Task RegistrarArchivoLocalConUploadAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            Guid? idUpload,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sqlV2 = @"
                INSERT INTO [dbo].[Transfer_ArchivosLocales]
                           ([RFC_Origen]
                           ,[RequerimientoId]
                           ,[ColaboraId]
                           ,[Nombre_Archivo]
                           ,[extension]
                           ,[Ruta_ARchivo_Fisico]
                           ,[Tamaño]
                           ,[Hash]
                           ,[Estatus]
                           ,[Fecha_Alta]
                           ,[Comentarios]
                           ,[Certificar]
                           ,[Accion]
                           ,[ID_Upload])
                     SELECT
                           @RFC_Origen
                           ,@RequerimientoId
                           ,@ColaboraId
                           ,@Nombre_Archivo
                           ,@extension
                           ,@Ruta_ARchivo_Fisico
                           ,@Tamano
                           ,@Hash
                           ,'A'
                           ,GETDATE()
                           ,N''
                           ,0
                           ,0
                           ,@ID_Upload
                     WHERE @ID_Upload IS NULL
                        OR NOT EXISTS (
                            SELECT 1
                            FROM [dbo].[Transfer_ArchivosLocales] WITH (UPDLOCK, HOLDLOCK)
                            WHERE [ID_Upload] = @ID_Upload
                        );";

            const string sqlV1 = @"
                INSERT INTO [dbo].[Transfer_ArchivosLocales]
                           ([RFC_Origen]
                           ,[RequerimientoId]
                           ,[ColaboraId]
                           ,[Nombre_Archivo]
                           ,[extension]
                           ,[Ruta_ARchivo_Fisico]
                           ,[Tamaño]
                           ,[Hash]
                           ,[Estatus]
                           ,[Fecha_Alta]
                           ,[Comentarios]
                           ,[Certificar]
                           ,[Accion])
                     VALUES
                           (@RFC_Origen
                           ,@RequerimientoId
                           ,@ColaboraId
                           ,@Nombre_Archivo
                           ,@extension
                           ,@Ruta_ARchivo_Fisico
                           ,@Tamano
                           ,@Hash
                           ,'A'
                           ,GETDATE()
                           ,N''
                           ,0
                           ,0);";

            var p = new DynamicParameters();
            var rutaEncriptada = ObtenerRutaEncriptada(rutaArchivoFisico);

            p.Add("@RFC_Origen", rfcOrigen?.Trim().ToUpperInvariant(), DbType.String, size: 15);
            p.Add("@RequerimientoId", requerimientoId, DbType.Int32);
            p.Add("@ColaboraId", colaboraId, DbType.Int32);
            p.Add("@Nombre_Archivo", nombreArchivo ?? string.Empty, DbType.String, size: 500);
            p.Add("@extension", extension ?? string.Empty, DbType.String, size: 4);
            p.Add("@Ruta_ARchivo_Fisico", rutaEncriptada ?? string.Empty, DbType.String, size: 500);
            p.Add("@Tamano", tamano ?? string.Empty, DbType.String, size: 50);
            p.Add("@Hash", hash8 ?? string.Empty, DbType.String, size: 8);
            p.Add("@ID_Upload", idUpload, DbType.Guid);

            try
            {
                await cn.ExecuteAsync(
                    new CommandDefinition(
                        sqlV2,
                        p,
                        commandType: CommandType.Text,
                        cancellationToken: ct));
            }
            catch (SqlException ex) when (ex.Number == 207 )
            {
                if (idUpload.HasValue)
                {
                    throw new InvalidOperationException(
                        "El esquema BuDi debe incluir Transfer_ArchivosLocales.ID_Upload para garantizar reintentos idempotentes.",
                        ex);
                }

                await cn.ExecuteAsync(
                    new CommandDefinition(
                        sqlV1,
                        p,
                        commandType: CommandType.Text,
                        cancellationToken: ct));
            }
        }

        public async Task RegistrarArchivoLocalPorResultadoAsync(
            string rfcOrigen,
            int resultadoId,
            int colaboraId,
            int? movimientoId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            CancellationToken ct)
        {
            await RegistrarArchivoLocalPorResultadoConUploadAsync(
                rfcOrigen,
                resultadoId,
                colaboraId,
                movimientoId,
                nombreArchivo,
                extension,
                rutaArchivoFisico,
                tamano,
                hash8,
                idUpload: null,
                ct);
        }

        /// <summary>
        /// Registra en Transfer_ArchivosLocales un archivo asociado a un resultado.
        ///
        /// Esta ruta cubre:
        /// - Acto 16: ResultadoId = BUZ_Resultados_Cedulas.ID.
        /// - Acto 21: ResultadoId = BUZ_Resultados_CedulaEvaluacion.ID.
        ///
        /// Para acto 21 es obligatorio conservar MovimientoId cuando exista, porque el mismo enlace
        /// puede tener resultados similares en más de un movimiento/oficio. ID_Upload conecta el
        /// registro de negocio con la fila técnica de Uploads generada por gRPC.
        ///
        /// Las variantes antiguas solo permanecen para consumidores legados sin ID_Upload. El flujo
        /// gRPC productivo exige ID_Upload + MovimientoId para impedir duplicados y aislar contexto.
        /// </summary>
        public async Task RegistrarArchivoLocalPorResultadoConUploadAsync(
            string rfcOrigen,
            int resultadoId,
            int colaboraId,
            int? movimientoId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            Guid? idUpload,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sqlV3 = @"
                INSERT INTO [dbo].[Transfer_ArchivosLocales]
                           ([RFC_Origen]
                           ,[ResultadoId]
                           ,[ColaboraId]
                           ,[MovimientoId]
                           ,[Nombre_Archivo]
                           ,[extension]
                           ,[Ruta_ARchivo_Fisico]
                           ,[Tamaño]
                           ,[Hash]
                           ,[Estatus]
                           ,[Fecha_Alta]
                           ,[Comentarios]
                           ,[Certificar]
                           ,[Accion]
                           ,[ID_Upload])
                     SELECT
                           @RFC_Origen
                           ,@ResultadoId
                           ,@ColaboraId
                           ,@MovimientoId
                           ,@Nombre_Archivo
                           ,@extension
                           ,@Ruta_ARchivo_Fisico
                           ,@Tamano
                           ,@Hash
                           ,'A'
                           ,GETDATE()
                           ,N''
                           ,0
                           ,0
                           ,@ID_Upload
                     WHERE @ID_Upload IS NULL
                        OR NOT EXISTS (
                            SELECT 1
                            FROM [dbo].[Transfer_ArchivosLocales] WITH (UPDLOCK, HOLDLOCK)
                            WHERE [ID_Upload] = @ID_Upload
                        );";

            const string sqlV2 = @"
                INSERT INTO [dbo].[Transfer_ArchivosLocales]
                           ([RFC_Origen]
                           ,[ResultadoId]
                           ,[ColaboraId]
                           ,[MovimientoId]
                           ,[Nombre_Archivo]
                           ,[extension]
                           ,[Ruta_ARchivo_Fisico]
                           ,[Tamaño]
                           ,[Hash]
                           ,[Estatus]
                           ,[Fecha_Alta]
                           ,[Comentarios]
                           ,[Certificar]
                           ,[Accion])
                     VALUES
                           (@RFC_Origen
                           ,@ResultadoId
                           ,@ColaboraId
                           ,@MovimientoId
                           ,@Nombre_Archivo
                           ,@extension
                           ,@Ruta_ARchivo_Fisico
                           ,@Tamano
                           ,@Hash
                           ,'A'
                           ,GETDATE()
                           ,N''
                           ,0
                           ,0);";

            const string sqlV1 = @"
                INSERT INTO [dbo].[Transfer_ArchivosLocales]
                           ([RFC_Origen]
                           ,[ResultadoId]
                           ,[ColaboraId]
                           ,[Nombre_Archivo]
                           ,[extension]
                           ,[Ruta_ARchivo_Fisico]
                           ,[Tamaño]
                           ,[Hash]
                           ,[Estatus]
                           ,[Fecha_Alta]
                           ,[Comentarios]
                           ,[Certificar]
                           ,[Accion])
                     VALUES
                           (@RFC_Origen
                           ,@ResultadoId
                           ,@ColaboraId
                           ,@Nombre_Archivo
                           ,@extension
                           ,@Ruta_ARchivo_Fisico
                           ,@Tamano
                           ,@Hash
                           ,'A'
                           ,GETDATE()
                           ,N''
                           ,0
                           ,0);";

            var p = new DynamicParameters();
            var rutaEncriptada = ObtenerRutaEncriptada(rutaArchivoFisico);

            p.Add("@RFC_Origen", rfcOrigen?.Trim().ToUpperInvariant(), DbType.String, size: 15);
            p.Add("@ResultadoId", resultadoId, DbType.Int32);
            p.Add("@ColaboraId", colaboraId, DbType.Int32);
            p.Add("@MovimientoId", movimientoId, DbType.Int32);
            p.Add("@Nombre_Archivo", nombreArchivo ?? string.Empty, DbType.String, size: 500);
            p.Add("@extension", extension ?? string.Empty, DbType.String, size: 4);
            p.Add("@Ruta_ARchivo_Fisico", rutaEncriptada ?? string.Empty, DbType.String, size: 500);
            p.Add("@Tamano", tamano ?? string.Empty, DbType.String, size: 50);
            p.Add("@Hash", hash8 ?? string.Empty, DbType.String, size: 8);
            p.Add("@ID_Upload", idUpload, DbType.Guid);

            try
            {
                await cn.ExecuteAsync(
                    new CommandDefinition(
                        sqlV3,
                        p,
                        commandType: CommandType.Text,
                        cancellationToken: ct));
            }
            catch (SqlException ex) when (ex.Number == 207 )
            {
                if (idUpload.HasValue)
                {
                    throw new InvalidOperationException(
                        "El esquema BuDi debe incluir Transfer_ArchivosLocales.ID_Upload y MovimientoId para garantizar idempotencia y aislamiento.",
                        ex);
                }

                try
                {
                    await cn.ExecuteAsync(
                        new CommandDefinition(
                            sqlV2,
                            p,
                            commandType: CommandType.Text,
                            cancellationToken: ct));
                }
                catch (SqlException ex2) when (ex2.Number == 207)
                {
                    await cn.ExecuteAsync(
                        new CommandDefinition(
                            sqlV1,
                            p,
                            commandType: CommandType.Text,
                            cancellationToken: ct));
                }
            }
        }

        public async Task<IReadOnlyList<ArchivoLocalDto>> GetArchivosLocalesAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sql = @"
                SELECT
                    [Id]                  AS IdArchivoLocal,
                    [Nombre_Archivo]      AS NombreArchivo,
                    [extension]           AS Extension,
                    [Tamaño]              AS Tamano,
                    [Fecha_Alta]          AS FechaAlta
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE
                  [RequerimientoId] = @RequerimientoId
                  AND [Estatus]     = 'A'
                  AND [RFC_Origen] = @RFC_Origen
                  AND [ColaboraId] = @ColaboraId
                ORDER BY [Fecha_Alta];";

            var p = new DynamicParameters();
            p.Add("@RFC_Origen", rfcOrigen?.Trim().ToUpperInvariant(), DbType.String, size: 15);
            p.Add("@RequerimientoId", requerimientoId, DbType.Int32);
            p.Add("@ColaboraId", colaboraId, DbType.Int32);

            var rows = await cn.QueryAsync<_ArchivoLocalDbRow>(
                new CommandDefinition(
                    sql,
                    p,
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            var list = new List<ArchivoLocalDto>();

            foreach (var row in rows)
            {
                list.Add(new ArchivoLocalDto
                {
                    IdArchivoLocal = row.IdArchivoLocal,
                    NombreArchivo = row.NombreArchivo ?? string.Empty,
                    Extension = row.Extension ?? string.Empty,
                    Tamano = row.Tamano ?? string.Empty,
                    FechaAlta = row.FechaAlta,
                    // Las rutas físicas pertenecen al servidor y nunca se exponen al cliente.
                    RutaFisica = string.Empty
                });
            }

            return list;
        }

        /// <summary>
        /// Consulta documentos registrados para un resultado.
        ///
        /// ColaboraId y MovimientoId son obligatorios en todos los flujos de resultado. En acto
        /// 16 MovimientoId contiene el ID del anexo; en acto 21 contiene el ID del oficio.
        /// </summary>
        public async Task<IReadOnlyList<ArchivoLocalDto>> GetArchivosLocalesPorResultadoAsync(
            string rfcOrigen,
            int resultadoId,
            int colaboraId,
            int movimientoId,
            CancellationToken ct)
        {
            await using var cn = Conn();

            const string sql = @"
                SELECT
                    [Id]                  AS IdArchivoLocal,
                    [Nombre_Archivo]      AS NombreArchivo,
                    [extension]           AS Extension,
                    [Tamaño]              AS Tamano,
                    [Fecha_Alta]          AS FechaAlta
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE
                  [ResultadoId] = @ResultadoId
                  AND [Estatus] = 'A'
                  AND [RFC_Origen] = @RfcOrigen
                  AND [ColaboraId] = @ColaboraId
                  AND [MovimientoId] = @MovimientoId
                ORDER BY [Fecha_Alta];";

            var rows = await cn.QueryAsync<_ArchivoLocalDbRow>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        RfcOrigen = rfcOrigen?.Trim().ToUpperInvariant(),
                        ResultadoId = resultadoId,
                        ColaboraId = colaboraId,
                        MovimientoId = movimientoId
                    },
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            var list = new List<ArchivoLocalDto>();

            foreach (var row in rows)
            {
                list.Add(new ArchivoLocalDto
                {
                    IdArchivoLocal = row.IdArchivoLocal,
                    NombreArchivo = row.NombreArchivo ?? string.Empty,
                    Extension = row.Extension ?? string.Empty,
                    Tamano = row.Tamano ?? string.Empty,
                    FechaAlta = row.FechaAlta,
                    RutaFisica = string.Empty
                });
            }

            return list;
        }

        public async Task<int> EliminarArchivosLocalesAsync(
            string rfcUsuario,
            int requerimientoId,
            int colaboraId,
            IReadOnlyCollection<int> idsArchivos,
            CancellationToken ct)
        {
            if (idsArchivos == null || idsArchivos.Count == 0)
                return 0;

            await using var cn = Conn();
            await cn.OpenAsync(ct);

            using var tx = cn.BeginTransaction();

            const string selectSql = @"
                SELECT
                    [Id]                  AS IdArchivoLocal,
                    [Ruta_ARchivo_Fisico] AS RutaEncriptada
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE [Id] IN @Ids
                  AND [RequerimientoId] = @RequerimientoId
                  AND [ColaboraId]      = @ColaboraId
                  AND [RFC_Origen]      = @RfcUsuario
                  AND [Estatus]         = 'A';";

            var selectParams = new
            {
                Ids = idsArchivos,
                RequerimientoId = requerimientoId,
                ColaboraId = colaboraId,
                RfcUsuario = rfcUsuario?.Trim().ToUpperInvariant()
            };

            var rows = (await cn.QueryAsync<_ArchivoLocalDbRow>(
                new CommandDefinition(
                    selectSql,
                    selectParams,
                    transaction: tx,
                    commandType: CommandType.Text,
                    cancellationToken: ct))).AsList();

            if (rows.Count == 0)
            {
                tx.Commit();
                return 0;
            }

            const string updateSql = @"
                UPDATE [dbo].[Transfer_ArchivosLocales]
                SET [Estatus] = 'B'
                WHERE [Id] IN @Ids
                  AND [RequerimientoId] = @RequerimientoId
                  AND [ColaboraId]      = @ColaboraId
                  AND [RFC_Origen]      = @RfcUsuario
                  AND [Estatus]         = 'A';";

            var eliminados = await cn.ExecuteAsync(
                new CommandDefinition(
                    updateSql,
                    selectParams,
                    transaction: tx,
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            // La solicitud realiza únicamente baja lógica. La limpieza física debe ejecutarse
            // fuera de la transacción mediante una tarea de retención que pueda comparar rutas
            // descifradas de todo el catálogo y recuperarse ante fallos. Borrar aquí exponía a
            // pérdida irreversible si el commit fallaba o dos ciphertexts protegían la misma ruta.
            tx.Commit();
            return eliminados;
        }

        /// <summary>
        /// Baja lógica de documentos asociados a un resultado.
        ///
        /// Marca Estatus='B' para conservar trazabilidad. La eliminación física queda fuera de la
        /// solicitud y requiere una política de retención auditable; así nunca se pierden bytes
        /// por un rollback o por referencias cifradas con nonces distintos.
        /// </summary>
        public async Task<int> EliminarArchivosLocalesPorResultadoAsync(
            string rfcUsuario,
            int resultadoId,
            int colaboraId,
            int movimientoId,
            IReadOnlyCollection<int> idsArchivos,
            CancellationToken ct)
        {
            if (idsArchivos == null || idsArchivos.Count == 0)
                return 0;

            await using var cn = Conn();
            await cn.OpenAsync(ct);

            using var tx = cn.BeginTransaction();

            const string selectSql = @"
                SELECT
                    [Id]                  AS IdArchivoLocal,
                    [Ruta_ARchivo_Fisico] AS RutaEncriptada
                FROM [dbo].[Transfer_ArchivosLocales]
                WHERE [Id] IN @Ids
                  AND [ResultadoId] = @ResultadoId
                  AND [RFC_Origen] = @RfcUsuario
                  AND [ColaboraId] = @ColaboraId
                  AND [MovimientoId] = @MovimientoId
                  AND [Estatus]     = 'A';";

            var selectParams = new
            {
                Ids = idsArchivos,
                ResultadoId = resultadoId,
                ColaboraId = colaboraId,
                MovimientoId = movimientoId,
                RfcUsuario = rfcUsuario?.Trim().ToUpperInvariant()
            };

            var rows = (await cn.QueryAsync<_ArchivoLocalDbRow>(
                new CommandDefinition(
                    selectSql,
                    selectParams,
                    transaction: tx,
                    commandType: CommandType.Text,
                    cancellationToken: ct))).AsList();

            if (rows.Count == 0)
            {
                tx.Commit();
                return 0;
            }

            const string updateSql = @"
                UPDATE [dbo].[Transfer_ArchivosLocales]
                SET [Estatus] = 'B'
                WHERE [Id] IN @Ids
                  AND [ResultadoId] = @ResultadoId
                  AND [RFC_Origen] = @RfcUsuario
                  AND [ColaboraId] = @ColaboraId
                  AND [MovimientoId] = @MovimientoId
                  AND [Estatus]     = 'A';";

            var eliminados = await cn.ExecuteAsync(
                new CommandDefinition(
                    updateSql,
                    selectParams,
                    transaction: tx,
                    commandType: CommandType.Text,
                    cancellationToken: ct));

            // Conserva el archivo físico hasta que una política de retención explícita lo
            // elimine de forma auditable. La baja interactiva nunca destruye bytes.
            tx.Commit();
            return eliminados;
        }

        private sealed class _ArchivoLocalDbRow
        {
            public int IdArchivoLocal { get; set; }
            public string NombreArchivo { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            public string Tamano { get; set; } = string.Empty;
            public DateTime FechaAlta { get; set; }
            public string RutaEncriptada { get; set; } = string.Empty;
        }

        private sealed class OficioDbRow
        {
            public int ID_Movimiento { get; set; }
            public string Oficio_Nombre { get; set; } = string.Empty;
        }

        private sealed class DbEnteAuditoria
        {
            public int Id_Colabora { get; set; }
            public int ID_Auditoria { get; set; }
            public string? Ente_Fiscalizado { get; set; }
            public string? Auditoria { get; set; }
        }

        private sealed class _ResultadoEvidenciaRow
        {
            public int IdResultado { get; set; }
            public int NumeroEvidencias { get; set; }
        }
    }
}
