// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Uploading/UploadGrpcService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Subsistema de carga de archivos: administra streaming gRPC, escritura física, hash, estado y registro final.
// Interacción: Recibe chunks desde TransferClienteASE, arma archivos en storage, valida integridad y persiste metadata en BuDi/FileXfer.
// Regla de mantenimiento: No marcar una carga como completa si falla la persistencia de negocio; diferenciar archivo recibido de archivo catalogado.
// Notas específicas:
// - Carga de archivos: el hash valida integridad física; el registro en Transfer_ArchivosLocales valida catalogación de negocio.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Proto;
using FileXfer.Pdf;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;


// ============================================================================
//  Servicio gRPC de carga física de archivos
// ============================================================================
//  Este servicio recibe archivos por streaming en bloques. El cliente envía metadatos
//  de contexto en cada UploadChunk: Ente, Auditoría, Acto, Oficio y una hoja final:
//      - IdRequerimiento para actos normales.
//      - ResultadoId para acto 16 o acto 21.
//
//  Al terminar el stream se valida hash SHA-256. Si la integridad es correcta, el servidor
//  registra la fila de negocio en Transfer_ArchivosLocales mediante BudiDevRepository.
//
//  Separación importante:
//  - Uploads: tabla técnica del servidor FileXfer para estado del stream.
//  - Transfer_ArchivosLocales: tabla de negocio usada por BuDi para saber qué documento
//    quedó anexado a qué requerimiento/resultado.
// ============================================================================
namespace FileXfer.Server.Uploading
{
    [Authorize]
    public class UploadGrpcService : Upload.UploadBase
    {
        private static readonly HashSet<string> BlockedExtensions = new(
            new[]
            {
                ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd",
                ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
                ".hta", ".jar", ".lnk", ".url", ".reg", ".chm", ".appx", ".msix",
                ".key", ".pem", ".pfx", ".p12", ".ppk", ".jks", ".keystore",
                ".kdbx", ".env", ".der"
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly FileAssembler _assembler;
        private readonly ILogger<UploadGrpcService> _log;
        private readonly IHttpContextAccessor _httpAccessor;
        private readonly IDbContextFactory<FileXferDbContext> _dbf;
        private readonly StorageOptions _storage;
        private readonly UploadSessionStore _sessionStore;
        private readonly IBudiDevRepository _budiDev;
        private readonly IPdfSafetyService _pdfSafety;

        public UploadGrpcService(
            FileAssembler assembler,
            ILogger<UploadGrpcService> log,
            IHttpContextAccessor httpAccessor,
            IDbContextFactory<FileXferDbContext> dbf,
            StorageOptions storage,
            UploadSessionStore sessionStore,
            IBudiDevRepository budiDev,
            IPdfSafetyService pdfSafety)
        {
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _httpAccessor = httpAccessor ?? throw new ArgumentNullException(nameof(httpAccessor));
            _dbf = dbf ?? throw new ArgumentNullException(nameof(dbf));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
            _budiDev = budiDev ?? throw new ArgumentNullException(nameof(budiDev));
            _pdfSafety = pdfSafety ?? throw new ArgumentNullException(nameof(pdfSafety));
        }

        /// <summary>
        /// Recibe un archivo completo como secuencia de chunks.
        ///
        /// El primer chunk crea o recupera la sesión de carga. Los chunks intermedios escriben
        /// bytes en disco y actualizan progreso. El chunk final llega con Data vacío y Sha256Hex;
        /// ahí se cierra el stream, se compara hash de cliente contra hash de servidor y se
        /// registra el archivo en Transfer_ArchivosLocales.
        /// </summary>
        public override async Task<UploadAck> UploadStream(
            IAsyncStreamReader<UploadChunk> requestStream,
            ServerCallContext context)
        {
            var http = _httpAccessor.HttpContext ?? context.GetHttpContext();
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var user = http.User;
            var rfc =
                user?.FindFirst("rfc")?.Value ??
                user?.FindFirst(ClaimsIdentity.DefaultNameClaimType)?.Value ??
                user?.FindFirst(ClaimTypes.Name)?.Value;

            if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var normalizedRfc))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "El token no contiene un RFC válido."));
            rfc = normalizedRfc;

            var deviceId =
                user?.FindFirst("device")?.Value ??
                user?.FindFirst("device_id")?.Value ??
                user?.FindFirst("deviceId")?.Value;

            int? idEnte = null;
            int? idAuditoria = null;
            int? idActo = null;
            int? idOficio = null;
            int? idRequerimiento = null;
            int? idResultado = null;
            int? periodo = null;

            string storageKey = rfc;

            string? fileUuid = null;
            string? fileName = null;
            string? sourcePath = null;
            string? clientSha256 = null;

            long lastOffset = 0;
            long totalBytes = 0;
            bool sessionStarted = false;
            bool contextAuthorized = false;

            UploadEntry? entry = null;
            string? storedFileNameForDisk = null; // nombre real en disco (puede incluir prefijo)
            string? serverSha256 = null;
            bool fileOwnedByAttempt = false;
            bool finalFileValidated = false;
            bool catalogOk = true;
            string? catalogFailure = null;
            string? attemptFailure = null;

            var lastDbFlushUtc = DateTimeOffset.UtcNow;
            long bytesSinceDbFlush = 0;
            const long DbFlushEveryBytes = 8L * 1024L * 1024L; // 8MB
            var DbFlushInterval = TimeSpan.FromSeconds(2);

            try
            {
                await _assembler.AuditAsync(
                    rfc,
                    "grpc_upload_begin",
                    ip,
                    "begin stream auth ok",
                    context.CancellationToken);

                await using var db = await _dbf.CreateDbContextAsync(context.CancellationToken);

                var basePath = _storage.BasePath;
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    throw new RpcException(new Status(
                        StatusCode.Internal,
                        "StorageOptions.BasePath vacío en servidor"));
                }

                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    var chunk = requestStream.Current;
                    if (chunk == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(fileUuid))
                    {
                        if (string.IsNullOrWhiteSpace(chunk.FileUuid))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "FileUuid requerido en el primer chunk"));

                        if (!Guid.TryParseExact(chunk.FileUuid, "N", out _))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "FileUuid no tiene el formato esperado"));

                        fileUuid = chunk.FileUuid;
                    }
                    else if (!string.IsNullOrWhiteSpace(chunk.FileUuid) &&
                             !string.Equals(chunk.FileUuid, fileUuid, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "FileUuid cambió dentro del mismo stream."));
                    }

                    if (!string.IsNullOrWhiteSpace(chunk.OriginalFileName) && fileName == null)
                    {
                        ValidateFileName(chunk.OriginalFileName);
                        fileName = chunk.OriginalFileName;
                    }
                    else if (!string.IsNullOrWhiteSpace(chunk.OriginalFileName) &&
                             !string.Equals(fileName, chunk.OriginalFileName, StringComparison.Ordinal))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "El nombre del archivo cambió dentro del mismo stream."));
                    }

                    // La ruta local del equipo cliente no es necesaria para procesar la carga y
                    // puede revelar nombres de usuario o estructura del puesto. Se ignora por diseño.

                    if (!string.IsNullOrWhiteSpace(chunk.Sha256Hex))
                    {
                        if (chunk.Sha256Hex.Length != 64 || !chunk.Sha256Hex.All(Uri.IsHexDigit))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "SHA-256 inválido."));

                        var incomingHash = chunk.Sha256Hex.ToLowerInvariant();
                        if (clientSha256 is not null &&
                            !string.Equals(clientSha256, incomingHash, StringComparison.Ordinal))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                "El SHA-256 cambió dentro del mismo stream."));
                        }
                        clientSha256 ??= incomingHash;
                    }

                    if (string.IsNullOrWhiteSpace(fileUuid))
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "FileUuid requerido"));

                    CaptureImmutablePositive(ref idEnte, chunk.IdEnte, nameof(chunk.IdEnte));
                    CaptureImmutablePositive(ref idAuditoria, chunk.IdAuditoria, nameof(chunk.IdAuditoria));
                    CaptureImmutablePositive(ref idActo, chunk.IdActo, nameof(chunk.IdActo));
                    CaptureImmutablePositive(ref idOficio, chunk.IdOficio, nameof(chunk.IdOficio));
                    CaptureImmutablePositive(ref idRequerimiento, chunk.IdRequerimiento, nameof(chunk.IdRequerimiento));
                    CaptureImmutablePositive(ref periodo, chunk.Periodo, nameof(chunk.Periodo));

                    // ResultadoId identifica el resultado que recibirá el archivo.
                    // - Acto 16: BUZ_Resultados_Cedulas.ID.
                    // - Acto 21: BUZ_Resultados_CedulaEvaluacion.ID.
                    // Si viene ResultadoId, el flujo se considera por resultado y no por requerimiento.
                    CaptureImmutablePositive(ref idResultado, chunk.ResultadoId, nameof(chunk.ResultadoId));

                    var tieneRequerimiento = idRequerimiento.GetValueOrDefault() > 0;
                    var tieneResultado = idResultado.GetValueOrDefault() > 0;
                    if (tieneRequerimiento == tieneResultado)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Debe permanecer exactamente uno de IdRequerimiento o ResultadoId durante toda la carga."));
                    }

                    if (!contextAuthorized)
                    {
                        if (!periodo.HasValue || !idEnte.HasValue || !idAuditoria.HasValue ||
                            !idActo.HasValue || !idOficio.HasValue)
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                "El contexto documental está incompleto o es ambiguo."));
                        }

                        contextAuthorized = await _budiDev.IsUploadContextAuthorizedAsync(
                            rfc,
                            periodo.Value,
                            idEnte.Value,
                            idAuditoria.Value,
                            idActo.Value,
                            idOficio.Value,
                            idRequerimiento,
                            idResultado,
                            context.CancellationToken);

                        if (!contextAuthorized)
                        {
                            await _assembler.AuditAsync(
                                rfc,
                                "grpc_upload_denied_context",
                                ip,
                                $"periodo={periodo}; ente={idEnte}; auditoria={idAuditoria}; acto={idActo}; oficio={idOficio}",
                                context.CancellationToken);
                            throw new RpcException(new Status(
                                StatusCode.PermissionDenied,
                                "El usuario no tiene autorización para cargar archivos en el contexto seleccionado."));
                        }
                    }

                    // leafId es la parte final de la ruta de almacenamiento.
                    // Para requerimientos se usa RequerimientoId; para cédulas/informe individual
                    // se usa ResultadoId. Así la carpeta física conserva el mismo árbol funcional
                    // que el usuario seleccionó en pantalla.
                    var leafId = idRequerimiento ?? idResultado;

                    if (idEnte.HasValue &&
                        idAuditoria.HasValue &&
                        idActo.HasValue &&
                        idOficio.HasValue &&
                        leafId.HasValue)
                    {
                        storageKey = $"{idEnte.Value}/{idAuditoria.Value}/{idActo.Value}/{idOficio.Value}/{leafId.Value}";
                    }

                    if (!sessionStarted)
                    {
                        var startResult = _sessionStore.TryBegin(fileUuid, rfc);
                        if (startResult == UploadStartResult.DuplicateUuid)
                        {
                            throw new RpcException(new Status(
                                StatusCode.AlreadyExists,
                                "Ya existe una carga activa con el mismo FileUuid."));
                        }
                        if (startResult is UploadStartResult.GlobalLimitReached or UploadStartResult.RfcLimitReached)
                        {
                            throw new RpcException(new Status(
                                StatusCode.ResourceExhausted,
                                startResult == UploadStartResult.RfcLimitReached
                                    ? "Se alcanzó el límite de cargas simultáneas para el RFC. Espere a que concluya una carga."
                                    : "El servidor alcanzó temporalmente su límite de cargas simultáneas."));
                        }
                        sessionStarted = true;
                    }

                    if (entry == null)
                    {
                        if (string.IsNullOrWhiteSpace(fileName))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "OriginalFileName es obligatorio."));

                        entry = await db.Uploads
                            .SingleOrDefaultAsync(x => x.FileUuid == fileUuid, context.CancellationToken);

                        var displayFileName = !string.IsNullOrWhiteSpace(fileName)
                            ? fileName
                            : (fileUuid + ".bin");

                        if (entry == null)
                        {
                            var uploadId = Guid.NewGuid();
                            storedFileNameForDisk = $"{uploadId:D}_{displayFileName}";

                            var absPath = _assembler.ResolveAbsolutePath(storageKey, fileUuid, storedFileNameForDisk);
                            if (string.IsNullOrWhiteSpace(absPath))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.Internal,
                                    "ResolveAbsolutePath devolvió ruta vacía"));
                            }

                            var relPath = _assembler.GetStoredRelativePath(absPath);

                            entry = new UploadEntry
                            {
                                Id = uploadId,
                                Rfc = rfc,
                                FileUuid = fileUuid,
                                OriginalFileName = displayFileName,
                                OriginalSourcePath = sourcePath,
                                StoredRelativePath = relPath,
                                Status = "Pending",
                                BytesReceived = 0,
                                CreatedUtc = DateTimeOffset.UtcNow,
                                DeviceId = deviceId,
                                IdEnte = idEnte,
                                IdAuditoria = idAuditoria,
                                IdActo = idActo,
                                IdOficio = idOficio,
                                IdRequerimiento = idRequerimiento,
                                IdResultado = idResultado,
                                Periodo = periodo
                            };

                            db.Uploads.Add(entry);
                            await db.SaveChangesAsync(context.CancellationToken);

                            lastDbFlushUtc = DateTimeOffset.UtcNow;
                            bytesSinceDbFlush = 0;
                        }
                        else
                        {
                            if (!string.Equals(entry.Rfc, rfc, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.AlreadyExists,
                                    "El FileUuid ya está registrado."));
                            }

                            if (string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.AlreadyExists,
                                    "La carga ya fue completada."));
                            }

                            if (!string.Equals(entry.OriginalFileName, displayFileName, StringComparison.Ordinal))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.AlreadyExists,
                                    "El nombre del archivo no coincide con el reintento original."));
                            }

                            if (entry.Periodo != periodo ||
                                entry.IdEnte != idEnte ||
                                entry.IdAuditoria != idAuditoria ||
                                entry.IdActo != idActo ||
                                entry.IdOficio != idOficio ||
                                entry.IdRequerimiento != idRequerimiento ||
                                entry.IdResultado != idResultado)
                            {
                                throw new RpcException(new Status(
                                    StatusCode.PermissionDenied,
                                    "El contexto de reintento no coincide con la carga original."));
                            }

                            storedFileNameForDisk = !string.IsNullOrWhiteSpace(entry.StoredRelativePath)
                                ? Path.GetFileName(entry.StoredRelativePath)
                                : null;

                            if (string.IsNullOrWhiteSpace(storedFileNameForDisk))
                            {
                                storedFileNameForDisk = $"{entry.Id:D}_{(entry.OriginalFileName ?? displayFileName)}";
                            }

                            var absPath = _assembler.ResolveAbsolutePath(storageKey, fileUuid, storedFileNameForDisk);
                            if (string.IsNullOrWhiteSpace(absPath))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.Internal,
                                    "ResolveAbsolutePath devolvió ruta vacía"));
                            }

                            var relPath = _assembler.GetStoredRelativePath(absPath);

                            entry.StoredRelativePath = relPath;
                            entry.BytesReceived = 0;
                            entry.Sha256Client = null;
                            entry.Sha256Server = null;
                            entry.Status = "Pending";
                            entry.LastError = null;
                            entry.CompletedUtc = null;

                            _assembler.ResetForRetry(storageKey, fileUuid, storedFileNameForDisk);
                            fileOwnedByAttempt = true;

                            await db.SaveChangesAsync(context.CancellationToken);

                            lastDbFlushUtc = DateTimeOffset.UtcNow;
                            bytesSinceDbFlush = 0;
                        }

                        storedFileNameForDisk ??= Path.GetFileName(entry.StoredRelativePath);
                        fileOwnedByAttempt = true;
                    }

                    if (entry != null)
                    {
                        bool updatedMeta = false;

                        if (!string.IsNullOrWhiteSpace(fileName) &&
                            !string.Equals(entry.OriginalFileName, fileName, StringComparison.Ordinal))
                        {
                            entry.OriginalFileName = fileName;
                            updatedMeta = true;
                        }

                        if (!string.IsNullOrWhiteSpace(sourcePath) &&
                            entry.OriginalSourcePath != sourcePath)
                        {
                            entry.OriginalSourcePath = sourcePath;
                            updatedMeta = true;
                        }

                        if (updatedMeta)
                        {
                            await db.SaveChangesAsync(context.CancellationToken);
                            lastDbFlushUtc = DateTimeOffset.UtcNow;
                            bytesSinceDbFlush = 0;
                        }
                    }

                    if (chunk.Offset < 0)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Offset inválido"));

                    if (chunk.Offset != lastOffset)
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Offset no contiguo. Esperado={lastOffset}, recibido={chunk.Offset}"));

                    var dataBytes = chunk.Data?.ToByteArray() ?? Array.Empty<byte>();
                    if (dataBytes.Length > 0)
                    {
                        if (dataBytes.Length > _storage.MaxChunkBytes)
                            throw new RpcException(new Status(StatusCode.ResourceExhausted, "El bloque excede el tamaño permitido"));
                        if (totalBytes > _storage.MaxUploadBytes - dataBytes.LongLength)
                            throw new RpcException(new Status(StatusCode.ResourceExhausted, "El archivo excede el tamaño máximo permitido"));

                        var currentFileNameForDisk =
                            storedFileNameForDisk
                            ?? (!string.IsNullOrWhiteSpace(entry!.StoredRelativePath)
                                ? Path.GetFileName(entry.StoredRelativePath)
                                : null)
                            ?? entry!.OriginalFileName
                            ?? (fileUuid + ".bin");

                        await _assembler.AppendAsync(
                            storageKey,
                            fileUuid!,
                            currentFileNameForDisk,
                            chunk.Offset,
                            dataBytes,
                            context.CancellationToken);

                        totalBytes += dataBytes.LongLength;
                        lastOffset += dataBytes.LongLength;

                        _sessionStore.Update(fileUuid!, dataBytes.LongLength);

                        entry!.BytesReceived = totalBytes;
                        bytesSinceDbFlush += dataBytes.LongLength;

                        var now = DateTimeOffset.UtcNow;
                        var needDbFlush =
                            bytesSinceDbFlush >= DbFlushEveryBytes ||
                            (now - lastDbFlushUtc) >= DbFlushInterval;

                        if (needDbFlush)
                        {
                            await db.SaveChangesAsync(context.CancellationToken);
                            lastDbFlushUtc = now;
                            bytesSinceDbFlush = 0;
                        }
                    }
                }

                if (fileUuid == null)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Stream sin datos"));

                if (entry != null && bytesSinceDbFlush > 0)
                {
                    await using var db2 = await _dbf.CreateDbContextAsync(context.CancellationToken);
                    var e2 = await db2.Uploads.SingleOrDefaultAsync(x => x.FileUuid == fileUuid, context.CancellationToken);
                    if (e2 != null)
                    {
                        e2.BytesReceived = totalBytes;
                        await db2.SaveChangesAsync(context.CancellationToken);
                    }
                }

                var finalFileNameForDisk =
                    storedFileNameForDisk
                    ?? (!string.IsNullOrWhiteSpace(entry?.StoredRelativePath)
                        ? Path.GetFileName(entry.StoredRelativePath)
                        : null)
                    ?? entry?.OriginalFileName
                    ?? (fileUuid + ".bin");

                serverSha256 = await _assembler.ComputeSha256Async(
                    storageKey,
                    fileUuid!,
                    finalFileNameForDisk,
                    context.CancellationToken);

                // Cierra el escritor antes de pedir a qpdf que abra el documento o de eliminar
                // una carga rechazada. End() en finally vuelve a intentarlo de forma idempotente.
                _assembler.ReleaseLock(storageKey, fileUuid!);

                var hashClienteValido = !string.IsNullOrWhiteSpace(clientSha256)
                                         && clientSha256.Length == 64
                                         && clientSha256.All(Uri.IsHexDigit);
                var integrityOk = hashClienteValido &&
                                  string.Equals(clientSha256, serverSha256, StringComparison.OrdinalIgnoreCase);
                string? validationFailure = integrityOk ? null : "SHA256 ausente, inválido o diferente";

                var absolutePath = _assembler.ResolveAbsolutePath(
                    storageKey,
                    fileUuid!,
                    finalFileNameForDisk);
                if (integrityOk && await LooksLikePortableExecutableAsync(absolutePath, context.CancellationToken))
                {
                    integrityOk = false;
                    validationFailure = "El contenido corresponde a un ejecutable de Windows y está bloqueado.";
                }

                var nombreOriginalParaValidar = entry?.OriginalFileName ?? fileName ?? finalFileNameForDisk;
                var extensionPdf = Path.GetExtension(nombreOriginalParaValidar)
                    .Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                var contenidoPdf = integrityOk &&
                    await LooksLikePdfAsync(absolutePath, context.CancellationToken);

                if (integrityOk && (extensionPdf || contenidoPdf))
                {
                    var pdfResult = await _pdfSafety.InspectAsync(
                        absolutePath,
                        context.CancellationToken);

                    if (!pdfResult.IsAccepted)
                    {
                        integrityOk = false;
                        validationFailure = pdfResult.UserMessage;
                        _log.LogWarning(
                            "PDF rechazado por validación de seguridad. RFC={Rfc}, Uuid={Uuid}, Resultado={Resultado}",
                            rfc,
                            fileUuid,
                            pdfResult.Outcome);

                    }
                    else if (!extensionPdf)
                    {
                        integrityOk = false;
                        validationFailure = "El contenido es PDF, pero la extensión del archivo no es .pdf.";
                    }
                }

                finalFileValidated = integrityOk;
                if (!integrityOk)
                {
                    try
                    {
                        _assembler.DeleteIfExists(storageKey, fileUuid!, finalFileNameForDisk);
                    }
                    catch (Exception deleteException)
                    {
                        _log.LogWarning(
                            deleteException,
                            "No se pudo eliminar el archivo rechazado. Uuid={Uuid}",
                            fileUuid);
                    }
                }

                if (entry != null)
                {
                    if (!string.IsNullOrWhiteSpace(fileName))
                        entry.OriginalFileName = fileName;
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                        entry.OriginalSourcePath = sourcePath;

                    entry.Sha256Server = serverSha256;
                    entry.Sha256Client = clientSha256;
                    if (integrityOk && idEnte.HasValue && (idRequerimiento.HasValue || idResultado.HasValue))
                    {
                        var nombreArchivoLocal = entry.OriginalFileName ?? finalFileNameForDisk ?? (fileUuid + ".bin");

                        var extensionLocal = Path.GetExtension(nombreArchivoLocal) ?? string.Empty;
                        if (extensionLocal.StartsWith(".", StringComparison.Ordinal))
                            extensionLocal = extensionLocal.Substring(1);
                        if (extensionLocal.Length > 4)
                            extensionLocal = extensionLocal.Substring(extensionLocal.Length - 4);

                        var rutaFisica = entry.StoredRelativePath ?? string.Empty;
                        var tamanoStr = totalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var hash8 = !string.IsNullOrWhiteSpace(serverSha256) && serverSha256!.Length >= 8
                            ? serverSha256.Substring(0, 8)
                            : (serverSha256 ?? string.Empty);

                        try
                        {
                            if (idResultado.HasValue && idResultado.Value > 0)
                            {
                                await _budiDev.RegistrarArchivoLocalPorResultadoConUploadAsync(
                                    rfcOrigen: rfc,
                                    resultadoId: idResultado.Value,
                                    colaboraId: idEnte.Value,
                                    movimientoId: idOficio,
                                    nombreArchivo: nombreArchivoLocal,
                                    extension: extensionLocal,
                                    rutaArchivoFisico: rutaFisica,
                                    tamano: tamanoStr,
                                    hash8: hash8,
                                    idUpload: entry.Id,
                                    ct: context.CancellationToken);
                            }
                            else
                            {
                                await _budiDev.RegistrarArchivoLocalConUploadAsync(
                                    rfcOrigen: rfc,
                                    requerimientoId: idRequerimiento!.Value,
                                    colaboraId: idEnte.Value,
                                    nombreArchivo: nombreArchivoLocal,
                                    extension: extensionLocal,
                                    rutaArchivoFisico: rutaFisica,
                                    tamano: tamanoStr,
                                    hash8: hash8,
                                    idUpload: entry.Id,
                                    ct: context.CancellationToken);
                            }
                        }
                        catch (Exception exReg)
                        {
                            catalogOk = false;
                            catalogFailure = "El archivo llegó íntegro, pero no pudo registrarse en el catálogo documental.";
                            _log.LogError(
                                exReg,
                                "Error al registrar en Transfer_ArchivosLocales. RFC={Rfc}, Req={Req}, Resultado={Resultado}, Colabora={Colabora}, Uuid={Uuid}",
                                rfc,
                                idRequerimiento,
                                idResultado,
                                idEnte,
                                fileUuid);
                        }
                    }

                    entry.Status = integrityOk
                        ? (catalogOk ? "Completed" : "CatalogFailed")
                        : "Failed";
                    entry.CompletedUtc = DateTimeOffset.UtcNow;
                    entry.LastError = validationFailure ?? catalogFailure;

                    await db.SaveChangesAsync(context.CancellationToken);
                }

                var operationSuccess = integrityOk && catalogOk;

                {
                    var safeNameForAudit = entry?.OriginalFileName ?? fileName ?? (fileUuid + ".bin");
                    var detailsEnd =
                        "end stream; " +
                        "fileUuid=" + fileUuid +
                        "; fileName=" + safeNameForAudit +
                        "; bytes=" + totalBytes +
                        "; sha256Prefix=" + (serverSha256?.Length >= 12 ? serverSha256[..12] : serverSha256) +
                        "; validation=" + (operationSuccess ? "ok" : "rejected") +
                        "; resultadoId=" + (idResultado?.ToString() ?? "") +
                        "; requerimientoId=" + (idRequerimiento?.ToString() ?? "");

                    try
                    {
                        await _assembler.AuditAsync(
                            rfc,
                            "grpc_upload_end",
                            ip,
                            detailsEnd,
                            context.CancellationToken);
                    }
                    catch (Exception auditException)
                    {
                        _log.LogWarning(
                            auditException,
                            "La carga terminó, pero no se pudo escribir la auditoría final. Uuid={Uuid}",
                            fileUuid);
                    }
                }

                return new UploadAck
                {
                    Success = operationSuccess,
                    IntegrityOk = integrityOk,
                    StoredPath = string.Empty,
                    Message = operationSuccess
                        ? "ok"
                        : (validationFailure ?? catalogFailure ?? "archivo rechazado")
                };
            }
            catch (RpcException rex)
            {
                attemptFailure = rex.Status.Detail;
                _log.LogError(rex, "RpcException en UploadStream. RFC={Rfc}, FileUuid={FileUuid}", rfc, fileUuid);
                throw;
            }
            catch (Exception ex)
            {
                attemptFailure = "La carga terminó por un error interno antes de completarse.";
                _log.LogError(ex, "Error en UploadStream. RFC={Rfc}, FileUuid={FileUuid}", rfc, fileUuid);

                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Error interno en el servidor de upload"));
            }
            finally
            {
                if (sessionStarted && fileUuid != null)
                {
                    _sessionStore.End(fileUuid);
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(fileUuid))
                    {
                        _assembler.ReleaseLock(storageKey, fileUuid);
                    }
                }
                catch
                {
                }


                if (fileOwnedByAttempt && !finalFileValidated &&
                    !string.IsNullOrWhiteSpace(fileUuid))
                {
                    try
                    {
                        var cleanupName = storedFileNameForDisk
                            ?? entry?.OriginalFileName
                            ?? (fileUuid + ".bin");
                        _assembler.DeleteIfExists(storageKey, fileUuid, cleanupName);
                    }
                    catch (Exception cleanupException)
                    {
                        _log.LogWarning(
                            cleanupException,
                            "No se pudo limpiar la carga incompleta. Uuid={Uuid}",
                            fileUuid);
                    }

                    try
                    {
                        await MarkPendingUploadFailedAsync(
                            fileUuid,
                            attemptFailure ?? "La carga no se completó.");
                    }
                    catch (Exception statusException)
                    {
                        _log.LogWarning(
                            statusException,
                            "No se pudo marcar la carga incompleta como fallida. Uuid={Uuid}",
                            fileUuid);
                    }
                }
            }
        }

        private async Task MarkPendingUploadFailedAsync(string fileUuid, string reason)
        {
            await using var db = await _dbf.CreateDbContextAsync(CancellationToken.None);
            var boundedReason = reason.Length <= 1024 ? reason : reason[..1024];
            if (db.Database.IsRelational())
            {
                await db.Uploads
                    .Where(upload => upload.FileUuid == fileUuid && upload.Status == "Pending")
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(upload => upload.Status, "Failed")
                            .SetProperty(upload => upload.LastError, boundedReason)
                            .SetProperty(upload => upload.CompletedUtc, DateTimeOffset.UtcNow),
                        CancellationToken.None);
                return;
            }

            var entry = await db.Uploads.SingleOrDefaultAsync(
                upload => upload.FileUuid == fileUuid && upload.Status == "Pending",
                CancellationToken.None);
            if (entry is null)
                return;

            entry.Status = "Failed";
            entry.LastError = boundedReason;
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        private static void ValidateFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "El nombre del archivo no es válido."));
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "El archivo debe tener una extensión."));
            if (BlockedExtensions.Contains(extension))
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "El tipo de archivo está bloqueado por la política de seguridad."));
            }
        }

        private static async Task<bool> LooksLikePdfAsync(string path, CancellationToken ct)
        {
            var header = new byte[1024];
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: header.Length,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
            for (var index = 0; index <= read - 5; index++)
            {
                if (header[index] == (byte)'%' &&
                    header[index + 1] == (byte)'P' &&
                    header[index + 2] == (byte)'D' &&
                    header[index + 3] == (byte)'F' &&
                    header[index + 4] == (byte)'-')
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> LooksLikePortableExecutableAsync(string path, CancellationToken ct)
        {
            var dosHeader = new byte[64];
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.RandomAccess);

            var read = await stream.ReadAsync(dosHeader.AsMemory(), ct);
            if (read < dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
                return false;

            var peOffset = BitConverter.ToInt32(dosHeader, 0x3c);
            if (peOffset < 64 || peOffset > 4 * 1024 * 1024 || peOffset > stream.Length - 4)
                return false;

            stream.Position = peOffset;
            var signature = new byte[4];
            if (await stream.ReadAsync(signature.AsMemory(), ct) != signature.Length)
                return false;

            return signature[0] == (byte)'P' &&
                   signature[1] == (byte)'E' &&
                   signature[2] == 0 &&
                   signature[3] == 0;
        }

        private static void CaptureImmutablePositive(ref int? current, int incoming, string fieldName)
        {
            if (incoming <= 0)
                return;

            if (current.HasValue && current.Value != incoming)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"{fieldName} cambió dentro del mismo stream."));
            }

            current ??= incoming;
        }
    }
}
