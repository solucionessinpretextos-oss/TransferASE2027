// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/AuditoriaCombosService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Services;
using FileXfer.Server.Dtos.Combos;   // <-- DTOs correctos

namespace FileXfer.Server.Services
{
    [Obsolete("Usa directamente IBudiDevRepository")]
    public sealed class AuditoriaCombosService
    {
        private readonly IBudiDevRepository _repo;

        public AuditoriaCombosService(IBudiDevRepository repo)
        {
            _repo = repo;
        }

        public Task<IReadOnlyList<AnioDto>> GetYearsAsync(int cuantos, CancellationToken ct)
            => _repo.GetYearsAsync(cuantos, ct);

        public Task<IReadOnlyList<EnteDto>> GetEntesAsync(string rfc, int periodo, CancellationToken ct)
            => _repo.GetEntesAsync(rfc, periodo, ct);

        public Task<IReadOnlyList<ActoDto>> GetActosAsync(CancellationToken ct)
            => _repo.GetActosAsync(ct);

        public Task<IReadOnlyList<OficioDto>> GetOficiosAsync(int idAuditoria, int idActo, int idColabora, CancellationToken ct)
            => _repo.GetOficiosAsync(idAuditoria, idActo, idColabora, ct);

        public Task<PagedResult<RequerimientoDto>> GetRequerimientosAsync(
            int idOficio, int idColabora, int skip, int take, string? search, CancellationToken ct)
            => _repo.GetRequerimientosAsync(idOficio, idColabora, skip, take, search, ct);
    }
}
