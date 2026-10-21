using System.Diagnostics;
using FileXfer.Server.Data;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers;

[ApiController]
[Authorize]
[Route("archivos-locales")]
public sealed class FileDownloadsController : ControllerBase
{
    private readonly IBudiDevRepository _budi;
    private readonly ISecureFileDownloadService _downloads;
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly IAlertSink _alerts;
    private readonly ILogger<FileDownloadsController> _logger;

    public FileDownloadsController(
        IBudiDevRepository budi,
        ISecureFileDownloadService downloads,
        IDbContextFactory<FileXferDbContext> dbFactory,
        IAlertSink alerts,
        ILogger<FileDownloadsController> logger)
    {
        _budi = budi;
        _downloads = downloads;
        _dbFactory = dbFactory;
        _alerts = alerts;
        _logger = logger;
    }

    [HttpGet("{idArchivoLocal:int}/download")]
    public async Task<IActionResult> Download(
        int idArchivoLocal,
        [FromQuery] string? rfc,
        CancellationToken cancellationToken)
    {
        if (idArchivoLocal <= 0)
            return BadRequest(new { message = "El identificador del archivo no es válido." });

        var callerRfc = User.FindFirst("rfc")?.Value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(callerRfc))
            return Unauthorized();

        var targetRfc = string.IsNullOrWhiteSpace(rfc)
            ? callerRfc
            : rfc.Trim().ToUpperInvariant();

        if (targetRfc.Length > 13 || !RfcResourceAuthorization.CanAccess(User, targetRfc))
            return Forbid();

        var descriptor = await _budi.GetArchivoLocalForDownloadAsync(
            idArchivoLocal,
            targetRfc,
            cancellationToken);
        if (descriptor is null)
            return NotFound();

        PreparedFileDownload prepared;
        var verificationStopwatch = Stopwatch.StartNew();
        try
        {
            prepared = await _downloads.PrepareAsync(descriptor, cancellationToken);
            ServerMetrics.FileDownloadVerificationDurationMs.Record(verificationStopwatch.Elapsed.TotalMilliseconds);
        }
        catch (FileDownloadPreparationException ex)
        {
            ServerMetrics.FileDownloadVerificationDurationMs.Record(verificationStopwatch.Elapsed.TotalMilliseconds);
            ServerMetrics.FileDownloadsBlockedTotal.Add(1);
            _logger.LogWarning(
                "Descarga bloqueada. ArchivoLocalId={ArchivoLocalId}, RFC={Rfc}, Code={Code}, TraceId={TraceId}",
                idArchivoLocal,
                targetRfc,
                ex.Code,
                HttpContext.TraceIdentifier);

            if (ex.Code is "hash_mismatch" or "length_mismatch" or "unsafe_storage_link" or "storage_reference_mismatch")
            {
                await _alerts.NotifySecurityEventAsync(
                    "Descarga bloqueada por integridad",
                    $"ArchivoLocalId={idArchivoLocal}; RFC={targetRfc}; Code={ex.Code}; TraceId={HttpContext.TraceIdentifier}",
                    cancellationToken);
            }

            var problem = new ProblemDetails
            {
                Title = "No es posible descargar el archivo de forma segura.",
                Detail = ex.Message,
                Status = StatusCodes.Status409Conflict
            };
            problem.Extensions["code"] = ex.Code;
            return StatusCode(StatusCodes.Status409Conflict, problem);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                ActorRfc = callerRfc,
                Action = "file_download_verified",
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Details =
                    $"ArchivoLocalId={idArchivoLocal}; UploadId={prepared.UploadId:D}; " +
                    $"TargetRfc={targetRfc}; Bytes={prepared.Length}; Sha256Prefix={prepared.Sha256[..12]}; " +
                    $"TraceId={HttpContext.TraceIdentifier}"
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await prepared.Stream.DisposeAsync();
            throw;
        }

        Response.Headers.CacheControl = "private, no-store, no-cache";
        Response.Headers.ETag = $"\"sha256-{prepared.Sha256}\"";
        Response.Headers["X-Content-SHA256"] = prepared.Sha256;
        Response.Headers["X-Upload-ID"] = prepared.UploadId.ToString("D");
        ServerMetrics.FileDownloadsPreparedTotal.Add(1);
        ServerMetrics.FileDownloadBytesPreparedTotal.Add(prepared.Length);

        return File(
            prepared.Stream,
            "application/octet-stream",
            prepared.FileName,
            enableRangeProcessing: true);
    }
}
