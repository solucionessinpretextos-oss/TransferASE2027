// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/IBudiDevRepository.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Notas específicas:
// - Regla Delta: en acto 21, ResultadoId siempre debe ser BUZ_Resultados_CedulaEvaluacion.ID, nunca BUZ_Resultados_Cedulas.ID.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Dtos.Combos;


// ============================================================================
//  Contrato de acceso a BuDi_dev
// ============================================================================
//  Esta interfaz documenta qué necesita el servidor FileXfer de la base BuDi.
//  Los controladores consumen esta abstracción y no SQL directamente. Eso permite:
//  - Cambiar consultas sin tocar endpoints REST.
//  - Centralizar reglas críticas de negocio como la diferencia entre acto 16 y acto 21.
//  - Probar controladores con dobles/mocks si se requiere más adelante.
// ============================================================================
namespace FileXfer.Server.Services
{
    public sealed record ArchivoLocalDownloadDescriptor(
        int IdArchivoLocal,
        string Rfc,
        string FileName,
        string StoredRelativePath,
        Guid? UploadId);

    /// <summary>
    /// Define todas las operaciones que FileXfer.Server necesita de BuDi_dev para alimentar
    /// combos, resultados y archivos registrados. La implementación concreta está en
    /// BudiDevRepository.
    /// </summary>
    public interface IBudiDevRepository
    {
        Task<IReadOnlyList<AnioDto>> GetYearsAsync(int cuantos, CancellationToken ct);

        Task<IReadOnlyList<EnteDto>> GetEntesAsync(string rfc, int periodo, CancellationToken ct);

        Task<bool> IsAuditContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            CancellationToken ct);

        Task<bool> IsOficioContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            CancellationToken ct);

        Task<bool> IsUploadContextAuthorizedAsync(
            string rfc,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int? idRequerimiento,
            int? idResultado,
            CancellationToken ct);

        Task<IReadOnlyList<ActoDto>> GetActosAsync(CancellationToken ct);

        Task<IReadOnlyList<OficioDto>> GetOficiosAsync(
            int idAuditoria,
            int idActo,
            int idColabora,
            CancellationToken ct);

        Task<PagedResult<RequerimientoDto>> GetRequerimientosAsync(
            int idOficio,
            int idColabora,
            int skip,
            int take,
            string? search,
            CancellationToken ct);

        Task RegistrarArchivoLocalAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            CancellationToken ct);

        Task RegistrarArchivoLocalConUploadAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            Guid? idUpload,
            CancellationToken ct);

        Task RegistrarArchivoLocalPorResultadoAsync(
            string rfcOrigen,
            int resultadoId,
            int colaboraId,
            int? movimientoId,
            string nombreArchivo,
            string extension,
            string rutaArchivoFisico,
            string tamano,
            string hash8,
            CancellationToken ct);

        Task RegistrarArchivoLocalPorResultadoConUploadAsync(
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
            CancellationToken ct);

        Task<IReadOnlyList<AnexoCedulaDto>> GetAnexosCedulasPorAuditoriaAsync(
            int idAuditoria,
            CancellationToken ct);

        Task<IReadOnlyList<ResultadoCedulaDto>> GetResultadosCedulasPorAuditoriaAsync(
            int idAuditoria,
            int idEnlace,
            CancellationToken ct);

        /// <summary>
        /// Resultados del acto 21. Deben salir de BUZ_Asignaciones_Resultados_Informe y guardar
        /// documentos contra BUZ_Resultados_CedulaEvaluacion.ID.
        /// </summary>
        Task<IReadOnlyList<ResultadoInformeDto>> GetResultadosInformeIndividualAsync(
            int idAuditoria,
            int idOficio,
            int idColabora,
            CancellationToken ct);

        Task<IReadOnlyList<ArchivoLocalDto>> GetArchivosLocalesAsync(
            string rfcOrigen,
            int requerimientoId,
            int colaboraId,
            CancellationToken ct);

        Task<IReadOnlyList<ArchivoLocalDto>> GetArchivosLocalesPorResultadoAsync(
            string rfcOrigen,
            int resultadoId,
            int colaboraId,
            int movimientoId,
            CancellationToken ct);

        Task<ArchivoLocalDownloadDescriptor?> GetArchivoLocalForDownloadAsync(
            int idArchivoLocal,
            string rfcOrigen,
            CancellationToken ct);

        Task<int> EliminarArchivosLocalesAsync(
            string rfcUsuario,
            int requerimientoId,
            int colaboraId,
            IReadOnlyCollection<int> idsArchivos,
            CancellationToken ct);
        Task<int> EliminarArchivosLocalesPorResultadoAsync(
            string rfcUsuario,
            int resultadoId,
            int colaboraId,
            int movimientoId,
            IReadOnlyCollection<int> idsArchivos,
            CancellationToken ct);

    }
}
