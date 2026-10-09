// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Endpoints/DebugUploadEndpoints.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Endpoints auxiliares o de diagnóstico: facilitan pruebas técnicas fuera del flujo principal.
// Interacción: Se conectan al pipeline HTTP y deben tratarse como herramientas controladas de desarrollo/soporte.
// Regla de mantenimiento: Proteger o deshabilitar en producción si exponen información sensible.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Uploading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace FileXfer.Server.Endpoints
{
    public static class DebugUploadEndpoints
    {
        public static IEndpointRouteBuilder MapDebugUpload(
            this IEndpointRouteBuilder app,
            IReadOnlyCollection<string> allowedRoles)
        {
            app.MapPost("/debug/upload-chunk", async (
                    HttpContext http,
                    FileAssembler assembler,
                    IDbContextFactory<FileXferDbContext> dbf,
                    StorageOptions storageOptions) =>
            {
                var ct = http.RequestAborted;

                if (http.Request.ContentLength is long contentLength &&
                    contentLength > storageOptions.MaxChunkBytes + 64 * 1024)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var rfc = http.User?.FindFirst("rfc")?.Value
                          ?? http.User?.FindFirst(ClaimTypes.Name)?.Value
                          ?? (http.Request.Query.TryGetValue("rfc", out var rfcQ) ? rfcQ.ToString() : "DEBUG");

                var fileUuid = http.Request.Query.TryGetValue("uuid", out var uuidQ)
                    ? uuidQ.ToString()
                    : "";
                if (string.IsNullOrWhiteSpace(fileUuid))
                    return Results.BadRequest(new { ok = false, message = "uuid requerido" });

                var fileName = http.Request.Query.TryGetValue("name", out var nameQ)
                    ? nameQ.ToString()
                    : null;

                var sourcePath = http.Request.Query.TryGetValue("src", out var srcQ)
                    ? srcQ.ToString()
                    : null;

                long offset = 0;
                if (http.Request.Query.TryGetValue("offset", out var offQ))
                    long.TryParse(offQ.ToString(), out offset);

                bool eof = false;
                if (http.Request.Query.TryGetValue("eof", out var eofQ))
                    bool.TryParse(eofQ.ToString(), out eof);

                string? sha256Hex = null;
                if (http.Request.Query.TryGetValue("sha256", out var shaQ))
                    sha256Hex = string.IsNullOrWhiteSpace(shaQ.ToString()) ? null : shaQ.ToString();

                byte[] data;
                if (http.Request.HasFormContentType)
                {
                    var form = await http.Request.ReadFormAsync(ct);
                    var file = form.Files.GetFile("chunk")
                             ?? form.Files.GetFile("file")
                             ?? form.Files.GetFile("data");

                    if (file != null)
                    {
                        using var ms = new System.IO.MemoryStream((int)file.Length);
                        await file.CopyToAsync(ms, ct);
                        data = ms.ToArray();

                        if (string.IsNullOrWhiteSpace(fileUuid) && form.TryGetValue("uuid", out var fu))
                            fileUuid = fu.ToString();

                        if (string.IsNullOrWhiteSpace(fileName) && form.TryGetValue("name", out var fn))
                            fileName = fn.ToString();

                        if (string.IsNullOrWhiteSpace(sourcePath) && form.TryGetValue("src", out var fsx))
                            sourcePath = fsx.ToString();

                        if (form.TryGetValue("offset", out var fo) && long.TryParse(fo.ToString(), out var o2))
                            offset = o2;

                        if (form.TryGetValue("eof", out var fe) && bool.TryParse(fe.ToString(), out var e2))
                            eof = e2;

                        if (form.TryGetValue("sha256", out var fs) && !string.IsNullOrWhiteSpace(fs.ToString()))
                            sha256Hex = fs.ToString();
                    }
                    else
                    {
                        if (form.TryGetValue("data", out var dv) && !string.IsNullOrEmpty(dv.ToString()))
                        {
                            var s = dv.ToString();
                            try
                            {
                                data = Convert.FromBase64String(s);
                            }
                            catch
                            {
                                data = Encoding.UTF8.GetBytes(s);
                            }
                        }
                        else
                        {
                            data = Array.Empty<byte>();
                        }
                    }
                }
                else
                {
                    using var ms = new System.IO.MemoryStream();
                    await http.Request.Body.CopyToAsync(ms, ct);
                    data = ms.ToArray();
                }

                (bool accepted, string reason, long bytes, bool retried) result =
                    await assembler.WriteChunkAsync(
                        rfc,
                        fileUuid,
                        fileName,
                        offset,
                        data,
                        eof,
                        sha256Hex,
                        ct);

                await using var db = await dbf.CreateDbContextAsync(ct);

                var absPath = assembler.ResolveAbsolutePath(rfc, fileUuid, fileName);
                var relPath = Path.GetRelativePath(storageOptions.BasePath, absPath)
                                  .Replace('\\', '/');

                var entry = await db.Uploads.SingleOrDefaultAsync(u => u.FileUuid == fileUuid, ct);
                if (entry == null)
                {
                    entry = new UploadEntry
                    {
                        Id = Guid.NewGuid(),
                        Rfc = rfc,
                        FileUuid = fileUuid,
                        OriginalFileName = fileName,
                        OriginalSourcePath = sourcePath,
                        StoredRelativePath = relPath,
                        BytesReceived = result.bytes,
                        Status = "Pending",
                        CreatedUtc = DateTimeOffset.UtcNow,
                        DeviceId = "DEBUG"
                    };
                    db.Uploads.Add(entry);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(entry.OriginalFileName) && !string.IsNullOrWhiteSpace(fileName))
                        entry.OriginalFileName = fileName;

                    if (string.IsNullOrWhiteSpace(entry.OriginalSourcePath) && !string.IsNullOrWhiteSpace(sourcePath))
                        entry.OriginalSourcePath = sourcePath;

                    if (string.IsNullOrWhiteSpace(entry.StoredRelativePath))
                        entry.StoredRelativePath = relPath;

                    if (result.bytes > 0)
                        entry.BytesReceived += result.bytes;
                }

                if (eof)
                {
                    var serverSha256 = await assembler.ComputeSha256Async(rfc, fileUuid, fileName, ct);
                    var clientSha = sha256Hex?.ToLowerInvariant();
                    var integrityOk = clientSha == null || clientSha == serverSha256;

                    entry.Sha256Server = serverSha256;
                    entry.Sha256Client = clientSha;
                    entry.Status = integrityOk ? "Completed" : "Failed";
                    entry.CompletedUtc = DateTimeOffset.UtcNow;
                    entry.LastError = integrityOk ? null : "SHA256 mismatch";
                }

                await db.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    ok = result.accepted,
                    reason = result.reason,
                    bytes = result.bytes,
                    retried = result.retried,
                    eof,
                    fileUuid,
                    fileName,
                    sourcePath,
                    storedRelativePath = relPath
                });
            })
                .RequireAuthorization(policy =>
                    policy.RequireRole((allowedRoles.Count > 0
                        ? allowedRoles
                        : new[] { "admin" }).ToArray()))
                .Accepts<IFormFile>("multipart/form-data")
                .Produces(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest);

            return app;
        }
    }
}
