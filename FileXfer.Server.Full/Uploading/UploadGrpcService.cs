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
using FileXfer.FileSafety;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using FileXfer.Server.Telemetry;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        private readonly FileAssembler _assembler;
        private readonly ILogger<UploadGrpcService> _log;
        private readonly IHttpContextAccessor _httpAccessor;
        private readonly IDbContextFactory<FileXferDbContext> _dbf;
        private readonly StorageOptions _storage;
        private readonly UploadSessionStore _sessionStore;
        private readonly IBudiDevRepository _budiDev;
        private readonly IFileSafetyService _fileSafety;
        private readonly IFilePolicyService _filePolicies;
        private readonly IUploadReceiptService _receipts;

        public UploadGrpcService(
            FileAssembler assembler,
            ILogger<UploadGrpcService> log,
            IHttpContextAccessor httpAccessor,
            IDbContextFactory<FileXferDbContext> dbf,
            StorageOptions storage,
            UploadSessionStore sessionStore,
            IBudiDevRepository budiDev,
            IFileSafetyService fileSafety,
            IFilePolicyService filePolicies,
            IUploadReceiptService receipts)
        {
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _httpAccessor = httpAccessor ?? throw new ArgumentNullException(nameof(httpAccessor));
            _dbf = dbf ?? throw new ArgumentNullException(nameof(dbf));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
            _budiDev = budiDev ?? throw new ArgumentNullException(nameof(budiDev));
            _fileSafety = fileSafety ?? throw new ArgumentNullException(nameof(fileSafety));
            _filePolicies = filePolicies ?? throw new ArgumentNullException(nameof(filePolicies));
            _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        }

        public override async Task<UploadStatusReply> GetUploadStatus(
            UploadStatusRequest request,
            ServerCallContext context)
        {
            using var activity = TransferTelemetry.Start("upload.resume.status", ActivityKind.Server);
            var http = _httpAccessor.HttpContext ?? context.GetHttpContext();
            var rfc = GetAuthenticatedRfc(http.User);
            var deviceId = GetAuthenticatedDevice(http.User);

            if (!Guid.TryParseExact(request.FileUuid, "N", out _))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "FileUuid no tiene el formato esperado."));
            ValidateFileName(request.OriginalFileName);
            if (request.TotalLength <= 0 || _storage.ExceedsUploadSizeLimit(request.TotalLength))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "El tamaño total no es válido."));
            if (!IsSha256(request.Sha256Hex))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "SHA-256 requerido para reanudar."));
            if (!string.IsNullOrWhiteSpace(request.DeviceId) &&
                !string.Equals(deviceId, request.DeviceId, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "El dispositivo no coincide con la sesión autenticada."));
            }

            await using var db = await _dbf.CreateDbContextAsync(context.CancellationToken);
            var entry = await db.Uploads.SingleOrDefaultAsync(
                upload => upload.FileUuid == request.FileUuid,
                context.CancellationToken);

            FilePolicySnapshot policy;
            try
            {
                policy = await _filePolicies.RequireAllowedAsync(
                    request.OriginalFileName,
                    request.TotalLength,
                    context.CancellationToken);
            }
            catch (FilePolicyConflictException exception)
            {
                ServerMetrics.UploadPolicyRejectedTotal.Add(1);
                throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
            }

            if (entry is not null)
            {
                EnsureResumeIdentity(entry, request, rfc, deviceId);
                if (string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Una carga técnica completada no basta para declarar éxito: el usuario
                        // puede haber dado de baja lógica el documento y luego volver a elegirlo.
                        // La confirmación idempotente debe asegurar también que el registro de
                        // negocio esté activo; de otro modo el cliente mostraría un recibo válido
                        // mientras el requerimiento continuaría con cero documentos visibles.
                        await AsegurarCatalogoActivoAsync(entry, rfc, context.CancellationToken);
                    }
                    catch (Exception exception)
                    {
                        _log.LogError(
                            exception,
                            "No se pudo confirmar el catálogo de una carga completada. RFC={Rfc}, UploadId={UploadId}, FileUuid={FileUuid}",
                            rfc,
                            entry.Id,
                            entry.FileUuid);
                        throw new RpcException(new Status(
                            StatusCode.Unavailable,
                            "La carga existe, pero no fue posible confirmar su registro documental. Intente nuevamente."));
                    }

                    return new UploadStatusReply
                    {
                        ResumeAllowed = false,
                        AlreadyCompleted = true,
                        NextOffset = entry.BytesReceived,
                        Status = entry.Status,
                        Message = "La carga y su registro documental ya están confirmados.",
                        ReceiptId = entry.ReceiptId?.ToString("D") ?? string.Empty,
                        TraceId = entry.TraceId ?? CurrentTraceId(request.CorrelationId),
                        PolicyVersion = entry.PolicyVersion ?? 0,
                        VerificationRequired = entry.VerificationRequired
                    };
                }
            }

            if (entry is null)
            {
                return new UploadStatusReply
                {
                    ResumeAllowed = true,
                    NextOffset = 0,
                    Status = "New",
                    Message = "Carga nueva lista para iniciar.",
                    PolicyVersion = policy.Version,
                    VerificationRequired = policy.RequiresVerification,
                    TraceId = CurrentTraceId(request.CorrelationId)
                };
            }

            if (_sessionStore.Sessions.ContainsKey(request.FileUuid))
            {
                return new UploadStatusReply
                {
                    ResumeAllowed = false,
                    NextOffset = entry.BytesReceived,
                    Status = "Active",
                    Message = "La carga ya está activa en otra conexión.",
                    TraceId = entry.TraceId ?? CurrentTraceId(request.CorrelationId),
                    PolicyVersion = entry.PolicyVersion ?? policy.Version,
                    VerificationRequired = entry.VerificationRequired
                };
            }

            // Failed implica que el contenido no superó integridad/seguridad y ya no debe
            // reutilizarse. CatalogFailed, en cambio, conserva un archivo íntegro: se permite
            // reabrir desde el largo completo para repetir idempotentemente catálogo/recibo.
            if (entry.Status is "Failed")
            {
                return new UploadStatusReply
                {
                    ResumeAllowed = false,
                    NextOffset = 0,
                    Status = entry.Status,
                    Message = entry.LastError ?? "La carga anterior no admite reanudación.",
                    TraceId = entry.TraceId ?? CurrentTraceId(request.CorrelationId),
                    PolicyVersion = entry.PolicyVersion ?? policy.Version,
                    VerificationRequired = entry.VerificationRequired
                };
            }

            var storageKey = BuildStorageKey(entry);
            var storedName = Path.GetFileName(entry.StoredRelativePath);
            var persistedLength = _assembler.ReconcileToCommittedLength(
                storageKey,
                entry.FileUuid,
                storedName,
                Math.Max(0, entry.BytesReceived));
            if (persistedLength < 0 || persistedLength > request.TotalLength)
                throw new RpcException(new Status(StatusCode.DataLoss, "La carga parcial tiene un tamaño inconsistente y no puede reanudarse."));

            entry.BytesReceived = persistedLength;
            entry.LastActivityUtc = DateTimeOffset.UtcNow;
            entry.ResumeExpiresUtc = DateTimeOffset.UtcNow.AddHours(_storage.ResumeRetentionHours);
            entry.Status = persistedLength > 0 ? "Paused" : "Pending";
            entry.PolicyExtension = policy.Extension;
            entry.PolicyVersion = policy.Version;
            entry.VerificationRequired = policy.RequiresVerification;
            await db.SaveChangesAsync(context.CancellationToken);

            activity?.SetTag("filexfer.upload.uuid", request.FileUuid);
            activity?.SetTag("filexfer.resume.offset", persistedLength);
            return new UploadStatusReply
            {
                ResumeAllowed = true,
                NextOffset = persistedLength,
                Status = entry.Status,
                Message = persistedLength > 0
                    ? $"Carga reanudable desde el byte {persistedLength}."
                    : "Carga lista para iniciar desde cero.",
                TraceId = entry.TraceId ?? CurrentTraceId(request.CorrelationId),
                PolicyVersion = policy.Version,
                VerificationRequired = policy.RequiresVerification
            };
        }

        private async Task AsegurarCatalogoActivoAsync(
            UploadEntry entry,
            string rfc,
            CancellationToken ct)
        {
            if (entry.Periodo is not > 0 ||
                entry.IdEnte is not > 0 ||
                entry.IdAuditoria is not > 0 ||
                entry.IdActo is not > 0 ||
                entry.IdOficio is not > 0 ||
                (entry.IdRequerimiento is not > 0 && entry.IdResultado is not > 0))
            {
                throw new InvalidOperationException(
                    "La carga completada no conserva un contexto documental válido.");
            }

            if (string.IsNullOrWhiteSpace(entry.StoredRelativePath) ||
                string.IsNullOrWhiteSpace(entry.Sha256Server))
            {
                throw new InvalidOperationException(
                    "La carga completada no conserva ruta o hash de servidor.");
            }

            var nombreFisico = Path.GetFileName(entry.StoredRelativePath);
            var longitudPersistida = _assembler.GetPersistedLength(
                BuildStorageKey(entry),
                entry.FileUuid,
                nombreFisico);
            if (longitudPersistida != entry.BytesReceived || longitudPersistida <= 0)
            {
                throw new IOException(
                    "El archivo físico de la carga completada no coincide con su longitud confirmada.");
            }

            var nombreArchivo = entry.OriginalFileName ?? nombreFisico;
            ValidateFileName(nombreArchivo);
            var extension = Path.GetExtension(nombreArchivo) ?? string.Empty;
            if (extension.StartsWith(".", StringComparison.Ordinal))
                extension = extension[1..];
            if (extension.Length > 4)
                extension = extension[^4..];

            var tamano = entry.BytesReceived.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hash8 = entry.Sha256Server.Length >= 8
                ? entry.Sha256Server[..8]
                : entry.Sha256Server;

            if (entry.IdResultado is > 0)
            {
                await _budiDev.RegistrarArchivoLocalPorResultadoConUploadAsync(
                    rfc,
                    entry.IdResultado.Value,
                    entry.IdEnte.Value,
                    entry.IdOficio,
                    nombreArchivo,
                    extension,
                    entry.StoredRelativePath,
                    tamano,
                    hash8,
                    entry.Id,
                    ct);
                return;
            }

            await _budiDev.RegistrarArchivoLocalConUploadAsync(
                rfc,
                entry.IdRequerimiento!.Value,
                entry.IdEnte.Value,
                nombreArchivo,
                extension,
                entry.StoredRelativePath,
                tamano,
                hash8,
                entry.Id,
                ct);
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
            using var uploadActivity = TransferTelemetry.Start("upload.stream", ActivityKind.Server);
            var uploadStopwatch = Stopwatch.StartNew();
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
            using var uploadLogScope = _log.BeginScope(new Dictionary<string, object>
            {
                ["TraceId"] = CurrentTraceId(null),
                ["CorrelationId"] = http.TraceIdentifier,
                ["Rfc"] = rfc
            });

            var admissionResult = _sessionStore.TryAcquireAdmission(rfc, out var admissionLease);
            if (admissionResult != UploadAdmissionResult.Started)
            {
                throw new RpcException(new Status(
                    StatusCode.ResourceExhausted,
                    admissionResult == UploadAdmissionResult.RfcLimitReached
                        ? "Se alcanzó el límite de cargas simultáneas para el RFC. Espere a que concluya una carga."
                        : "El servidor alcanzó temporalmente su límite de cargas simultáneas."));
            }
            using var uploadAdmission = admissionLease!;

            using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            if (_storage.MaxUploadDurationMinutes > 0)
                uploadTimeout.CancelAfter(TimeSpan.FromMinutes(_storage.MaxUploadDurationMinutes));
            var uploadCancellationToken = uploadTimeout.Token;

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
            string? correlationId = null;
            long? expectedLength = null;
            FilePolicySnapshot? effectivePolicy = null;

            long lastOffset = 0;
            long totalBytes = 0;
            long resumedBytes = 0;
            long lastCommittedOffset = 0;
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
            bool preserveForResume = false;
            bool verificationPerformed = false;
            string verificationOutcome = "not-started";
            UploadReceipt? receipt = null;

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
                    uploadCancellationToken);

                await using var db = await _dbf.CreateDbContextAsync(uploadCancellationToken);

                var basePath = _storage.BasePath;
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    throw new RpcException(new Status(
                        StatusCode.Internal,
                        "StorageOptions.BasePath vacío en servidor"));
                }

                var nextChunkTimeout = TimeSpan.FromSeconds(_storage.FirstChunkTimeoutSeconds);
                while (await MoveNextWithTimeoutAsync(
                           requestStream,
                           nextChunkTimeout,
                           uploadCancellationToken,
                           context.CancellationToken))
                {
                    nextChunkTimeout = TimeSpan.FromSeconds(_storage.IdleChunkTimeoutSeconds);
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

                    if (chunk.TotalLength < 0 || _storage.ExceedsUploadSizeLimit(chunk.TotalLength))
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "TotalLength debe respetar el límite del servidor."));
                    if (chunk.TotalLength > 0)
                    {
                        if (!expectedLength.HasValue)
                            expectedLength = chunk.TotalLength;
                        else if (expectedLength.Value != chunk.TotalLength)
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "TotalLength cambió dentro del mismo stream."));
                    }

                    if (!string.IsNullOrWhiteSpace(chunk.CorrelationId))
                    {
                        if (!Guid.TryParse(chunk.CorrelationId, out _))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "CorrelationId no es válido."));
                        if (correlationId is null)
                        {
                            correlationId = chunk.CorrelationId;
                            uploadActivity?.SetTag("filexfer.correlation_id", correlationId);
                        }
                        else if (!string.Equals(correlationId, chunk.CorrelationId, StringComparison.OrdinalIgnoreCase))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "CorrelationId cambió dentro del mismo stream."));
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
                            uploadCancellationToken);

                        if (!contextAuthorized)
                        {
                            await _assembler.AuditAsync(
                                rfc,
                                "grpc_upload_denied_context",
                                ip,
                                $"periodo={periodo}; ente={idEnte}; auditoria={idAuditoria}; acto={idActo}; oficio={idOficio}",
                                uploadCancellationToken);
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

                    if (effectivePolicy is null)
                    {
                        if (string.IsNullOrWhiteSpace(fileName))
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Falta el nombre para evaluar la política de archivo."));
                        try
                        {
                            effectivePolicy = await _filePolicies.RequireAllowedAsync(
                                fileName,
                                expectedLength ?? 0,
                                uploadCancellationToken);
                        }
                        catch (FilePolicyConflictException exception)
                        {
                            ServerMetrics.UploadPolicyRejectedTotal.Add(1);
                            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
                        }

                        uploadActivity?.SetTag("filexfer.policy.extension", effectivePolicy.Extension);
                        uploadActivity?.SetTag("filexfer.policy.version", effectivePolicy.Version);
                        uploadActivity?.SetTag("filexfer.policy.verify", effectivePolicy.RequiresVerification);
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
                            .SingleOrDefaultAsync(x => x.FileUuid == fileUuid, uploadCancellationToken);

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
                                ExpectedLength = expectedLength,
                                ExpectedSha256 = clientSha256,
                                ResumedBytes = 0,
                                CreatedUtc = DateTimeOffset.UtcNow,
                                LastActivityUtc = DateTimeOffset.UtcNow,
                                ResumeExpiresUtc = DateTimeOffset.UtcNow.AddHours(_storage.ResumeRetentionHours),
                                PolicyExtension = effectivePolicy.Extension,
                                PolicyVersion = effectivePolicy.Version,
                                VerificationRequired = effectivePolicy.RequiresVerification,
                                VerificationPerformed = false,
                                VerificationOutcome = "pending",
                                TraceId = CurrentTraceId(correlationId),
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
                            await db.SaveChangesAsync(uploadCancellationToken);

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

                            if (!expectedLength.HasValue || string.IsNullOrWhiteSpace(clientSha256) ||
                                entry.ExpectedLength != expectedLength ||
                                !string.Equals(entry.ExpectedSha256, clientSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.FailedPrecondition,
                                    "El tamaño o SHA-256 no coincide con la carga parcial original."));
                            }

                            if (!string.IsNullOrWhiteSpace(entry.DeviceId) &&
                                !string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal))
                            {
                                throw new RpcException(new Status(
                                    StatusCode.PermissionDenied,
                                    "La carga parcial pertenece a otro dispositivo."));
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
                            var persistedLength = _assembler.ReconcileToCommittedLength(
                                storageKey,
                                fileUuid,
                                storedFileNameForDisk,
                                Math.Max(0, entry.BytesReceived));
                            if (persistedLength < 0 || persistedLength > expectedLength.Value)
                            {
                                throw new RpcException(new Status(
                                    StatusCode.DataLoss,
                                    "El archivo parcial tiene un tamaño inconsistente."));
                            }

                            entry.BytesReceived = persistedLength;
                            entry.Sha256Client = clientSha256;
                            entry.Sha256Server = null;
                            entry.Status = "Pending";
                            entry.LastError = null;
                            entry.CompletedUtc = null;
                            entry.LastActivityUtc = DateTimeOffset.UtcNow;
                            entry.ResumeExpiresUtc = DateTimeOffset.UtcNow.AddHours(_storage.ResumeRetentionHours);
                            entry.PolicyExtension = effectivePolicy.Extension;
                            entry.PolicyVersion = effectivePolicy.Version;
                            entry.VerificationRequired = effectivePolicy.RequiresVerification;
                            entry.VerificationPerformed = false;
                            entry.VerificationOutcome = "pending";
                            entry.TraceId ??= CurrentTraceId(correlationId);

                            lastOffset = persistedLength;
                            totalBytes = persistedLength;
                            resumedBytes = persistedLength;
                            lastCommittedOffset = persistedLength;
                            entry.ResumedBytes = checked(entry.ResumedBytes + persistedLength);
                            fileOwnedByAttempt = true;

                            if (persistedLength > 0)
                            {
                                ServerMetrics.UploadResumedFilesTotal.Add(1);
                                ServerMetrics.UploadResumedBytesTotal.Add(persistedLength);
                            }

                            await db.SaveChangesAsync(uploadCancellationToken);

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
                            await db.SaveChangesAsync(uploadCancellationToken);
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
                        if (_storage.WouldExceedUploadSizeLimit(totalBytes, dataBytes.LongLength))
                            throw new RpcException(new Status(StatusCode.ResourceExhausted, "El archivo excede el tamaño máximo permitido"));
                        if (expectedLength.HasValue && totalBytes > expectedLength.Value - dataBytes.LongLength)
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Se recibieron más bytes que TotalLength."));

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
                            uploadCancellationToken);

                        totalBytes += dataBytes.LongLength;
                        lastOffset += dataBytes.LongLength;
                        ServerMetrics.UploadChunksTotal.Add(1);
                        ServerMetrics.UploadBytesTotal.Add(dataBytes.LongLength);

                        _sessionStore.Update(fileUuid!, dataBytes.LongLength);

                        entry!.BytesReceived = totalBytes;
                        entry.LastActivityUtc = DateTimeOffset.UtcNow;
                        entry.ResumeExpiresUtc = DateTimeOffset.UtcNow.AddHours(_storage.ResumeRetentionHours);
                        bytesSinceDbFlush += dataBytes.LongLength;

                        var now = DateTimeOffset.UtcNow;
                        var needDbFlush =
                            bytesSinceDbFlush >= DbFlushEveryBytes ||
                            (now - lastDbFlushUtc) >= DbFlushInterval;

                        if (needDbFlush)
                        {
                            var durableLength = await _assembler.FlushToDiskAsync(
                                fileUuid!, uploadCancellationToken);
                            if (durableLength != totalBytes)
                            {
                                throw new RpcException(new Status(
                                    StatusCode.DataLoss,
                                    "El almacenamiento no confirmó la longitud esperada del archivo parcial."));
                            }
                            entry.BytesReceived = durableLength;
                            await db.SaveChangesAsync(uploadCancellationToken);
                            lastCommittedOffset = durableLength;
                            lastDbFlushUtc = now;
                            bytesSinceDbFlush = 0;
                        }
                    }
                }

                if (fileUuid == null)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Stream sin datos"));
                if (expectedLength.HasValue && totalBytes != expectedLength.Value)
                {
                    throw new RpcException(new Status(
                        StatusCode.FailedPrecondition,
                        $"La carga está incompleta. Esperado={expectedLength}, recibido={totalBytes}."));
                }
                if (totalBytes <= 0)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "El archivo está vacío."));
                if (effectivePolicy?.MaxFileBytes is long policyLimit && totalBytes > policyLimit)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "El archivo excede el límite configurado para su extensión."));

                if (entry is not null)
                {
                    entry.ExpectedLength ??= totalBytes;
                    entry.ExpectedSha256 ??= clientSha256;
                }

                if (entry != null && bytesSinceDbFlush > 0)
                {
                    var durableLength = await _assembler.FlushToDiskAsync(
                        fileUuid, uploadCancellationToken);
                    if (durableLength != totalBytes)
                    {
                        throw new RpcException(new Status(
                            StatusCode.DataLoss,
                            "El almacenamiento no confirmó todos los bytes recibidos."));
                    }
                    await using var db2 = await _dbf.CreateDbContextAsync(uploadCancellationToken);
                    var e2 = await db2.Uploads.SingleOrDefaultAsync(x => x.FileUuid == fileUuid, uploadCancellationToken);
                    if (e2 != null)
                    {
                        e2.BytesReceived = durableLength;
                        await db2.SaveChangesAsync(uploadCancellationToken);
                        lastCommittedOffset = durableLength;
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
                    uploadCancellationToken);

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
                if (integrityOk && await LooksLikePortableExecutableAsync(absolutePath, uploadCancellationToken))
                {
                    integrityOk = false;
                    validationFailure = "El contenido corresponde a un ejecutable de Windows y está bloqueado.";
                }

                var nombreOriginalParaValidar = entry?.OriginalFileName ?? fileName ?? finalFileNameForDisk;
                if (integrityOk && effectivePolicy?.RequiresVerification != false)
                {
                    using var validationActivity = TransferTelemetry.Start("upload.validate");
                    var validationStopwatch = Stopwatch.StartNew();
                    FileInspectionResult fileResult;
                    try
                    {
                        fileResult = await _fileSafety.InspectAsync(
                            absolutePath,
                            nombreOriginalParaValidar,
                            progress: null,
                            uploadCancellationToken);
                    }
                    finally
                    {
                        ServerMetrics.UploadValidationDurationMs.Record(validationStopwatch.Elapsed.TotalMilliseconds);
                    }
                    verificationPerformed = true;
                    verificationOutcome = fileResult.Outcome.ToString();
                    validationActivity?.SetTag("filexfer.validation.outcome", verificationOutcome);

                    if (!fileResult.IsAccepted)
                    {
                        integrityOk = false;
                        validationFailure = fileResult.UserMessage;
                        _log.LogWarning(
                            "Archivo rechazado por validación de seguridad. RFC={Rfc}, Uuid={Uuid}, Formato={Formato}, Resultado={Resultado}",
                            rfc,
                            fileUuid,
                            fileResult.Format,
                            fileResult.Outcome);
                    }
                }
                else if (integrityOk)
                {
                    verificationPerformed = false;
                    verificationOutcome = "BypassedByPolicy";
                    ServerMetrics.UploadVerificationBypassedTotal.Add(1);
                    _log.LogWarning(
                        "La verificación profunda fue omitida por política. RFC={Rfc}, Uuid={Uuid}, Extension={Extension}, PolicyVersion={PolicyVersion}, TraceId={TraceId}",
                        rfc,
                        fileUuid,
                        effectivePolicy?.Extension,
                        effectivePolicy?.Version,
                        CurrentTraceId(correlationId));
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
                    entry.VerificationRequired = effectivePolicy?.RequiresVerification ?? true;
                    entry.VerificationPerformed = verificationPerformed;
                    entry.VerificationOutcome = verificationOutcome;
                    entry.PolicyExtension = effectivePolicy?.Extension;
                    entry.PolicyVersion = effectivePolicy?.Version;
                    entry.TraceId ??= CurrentTraceId(correlationId);
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

                        using var catalogActivity = TransferTelemetry.Start("upload.catalog");
                        var catalogStopwatch = Stopwatch.StartNew();
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
                                    ct: uploadCancellationToken);
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
                                    ct: uploadCancellationToken);
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
                        finally
                        {
                            ServerMetrics.UploadCatalogDurationMs.Record(catalogStopwatch.Elapsed.TotalMilliseconds);
                        }
                    }

                    if (integrityOk && catalogOk && effectivePolicy is not null)
                    {
                        try
                        {
                            using var receiptActivity = TransferTelemetry.Start("upload.receipt.issue");
                            receipt = await _receipts.CreateAsync(
                                new UploadReceiptDraft(
                                    entry.Id,
                                    fileUuid!,
                                    entry.OriginalFileName ?? finalFileNameForDisk ?? (fileUuid + ".bin"),
                                    totalBytes,
                                    serverSha256!,
                                    rfc,
                                    deviceId,
                                    periodo,
                                    idEnte,
                                    idAuditoria,
                                    idActo,
                                    idOficio,
                                    idRequerimiento,
                                    idResultado,
                                    effectivePolicy.Extension,
                                    effectivePolicy.Version,
                                    effectivePolicy.RequiresVerification,
                                    verificationPerformed,
                                    verificationOutcome,
                                    entry.TraceId ?? CurrentTraceId(correlationId)),
                                uploadCancellationToken);
                            entry.ReceiptId = receipt.Id;
                        }
                        catch (Exception receiptException)
                        {
                            catalogOk = false;
                            catalogFailure = "El archivo fue validado y catalogado, pero no pudo emitirse el recibo digital.";
                            _log.LogError(
                                receiptException,
                                "No se pudo emitir el recibo digital. RFC={Rfc}, Uuid={Uuid}, TraceId={TraceId}",
                                rfc,
                                fileUuid,
                                entry.TraceId);
                        }
                    }

                    entry.Status = integrityOk
                        ? (catalogOk ? "Completed" : "CatalogFailed")
                        : "Failed";
                    entry.CompletedUtc = DateTimeOffset.UtcNow;
                    entry.LastActivityUtc = DateTimeOffset.UtcNow;
                    entry.ResumeExpiresUtc = null;
                    entry.LastError = validationFailure ?? catalogFailure;

                    await db.SaveChangesAsync(uploadCancellationToken);
                }

                var operationSuccess = integrityOk && catalogOk;
                if (operationSuccess)
                    ServerMetrics.UploadFilesCompletedTotal.Add(1);
                else
                    ServerMetrics.UploadFilesFailedTotal.Add(1);

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
                            uploadCancellationToken);
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
                        : (validationFailure ?? catalogFailure ?? "archivo rechazado"),
                    ReceiptId = receipt?.Id.ToString("D") ?? string.Empty,
                    TraceId = entry?.TraceId ?? CurrentTraceId(correlationId),
                    ResumedBytes = resumedBytes,
                    PolicyVersion = effectivePolicy?.Version ?? 0,
                    VerificationPerformed = verificationPerformed,
                    VerificationRequired = effectivePolicy?.RequiresVerification ?? true
                };
            }
            catch (RpcException rex)
            {
                attemptFailure = rex.Status.Detail;
                preserveForResume = rex.StatusCode is
                    StatusCode.Cancelled or
                    StatusCode.DeadlineExceeded or
                    StatusCode.Unavailable or
                    StatusCode.ResourceExhausted or
                    StatusCode.Internal;
                _log.LogError(rex, "RpcException en UploadStream. RFC={Rfc}, FileUuid={FileUuid}", rfc, fileUuid);
                throw;
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                attemptFailure = "La carga excedió el tiempo máximo permitido.";
                preserveForResume = true;
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, attemptFailure));
            }
            catch (OperationCanceledException)
            {
                attemptFailure = "La carga fue cancelada por el cliente.";
                preserveForResume = true;
                throw new RpcException(new Status(StatusCode.Cancelled, attemptFailure));
            }
            catch (Exception ex)
            {
                attemptFailure = "La carga terminó por un error interno antes de completarse.";
                preserveForResume = true;
                _log.LogError(ex, "Error en UploadStream. RFC={Rfc}, FileUuid={FileUuid}", rfc, fileUuid);

                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Error interno en el servidor de upload"));
            }
            finally
            {
                ServerMetrics.UploadFileTotalMs.Record(uploadStopwatch.Elapsed.TotalMilliseconds);
                uploadActivity?.SetTag("filexfer.upload.bytes", totalBytes);
                uploadActivity?.SetTag("filexfer.upload.resumed_bytes", resumedBytes);
                uploadActivity?.SetTag("filexfer.upload.preserved", preserveForResume && !finalFileValidated);
                // La cuota protege solicitudes activas, no tareas de limpieza. Se libera antes
                // de cualquier I/O de base de datos para que una falla de SQL no agote todos
                // los cupos después de cancelar o expirar cargas. Dispose es idempotente.
                uploadAdmission.Dispose();

                var pausedOffset = lastCommittedOffset;
                if (fileOwnedByAttempt && !finalFileValidated && preserveForResume &&
                    totalBytes > lastCommittedOffset && !string.IsNullOrWhiteSpace(fileUuid))
                {
                    try
                    {
                        using var durableFlushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var durableLength = await _assembler.FlushToDiskAsync(
                            fileUuid,
                            durableFlushTimeout.Token);
                        if (durableLength == totalBytes)
                            pausedOffset = durableLength;
                    }
                    catch (Exception flushException)
                    {
                        _log.LogWarning(
                            flushException,
                            "No se confirmó el último segmento; se reanudará desde el checkpoint anterior. Uuid={Uuid}, Offset={Offset}",
                            fileUuid,
                            lastCommittedOffset);
                    }
                }

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


                if (fileOwnedByAttempt && !finalFileValidated && preserveForResume &&
                    totalBytes > 0 && expectedLength.HasValue &&
                    !string.IsNullOrWhiteSpace(clientSha256) &&
                    !string.IsNullOrWhiteSpace(fileUuid))
                {
                    try
                    {
                        await MarkPendingUploadPausedAsync(
                            fileUuid,
                            pausedOffset,
                            attemptFailure ?? "La conexión se interrumpió; la carga puede reanudarse.");
                    }
                    catch (Exception statusException)
                    {
                        _log.LogWarning(
                            statusException,
                            "No se pudo marcar la carga como reanudable. Uuid={Uuid}",
                            fileUuid);
                    }
                }
                else if (fileOwnedByAttempt && !finalFileValidated &&
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

        private async Task MarkPendingUploadPausedAsync(string fileUuid, long bytesReceived, string reason)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var db = await _dbf.CreateDbContextAsync(timeout.Token);
            var entry = await db.Uploads.SingleOrDefaultAsync(
                upload => upload.FileUuid == fileUuid && upload.Status == "Pending",
                timeout.Token);
            if (entry is null)
                return;

            entry.Status = "Paused";
            entry.BytesReceived = bytesReceived;
            entry.LastActivityUtc = DateTimeOffset.UtcNow;
            entry.ResumeExpiresUtc = DateTimeOffset.UtcNow.AddHours(_storage.ResumeRetentionHours);
            entry.LastError = reason.Length <= 1024 ? reason : reason[..1024];
            entry.CompletedUtc = null;
            await db.SaveChangesAsync(timeout.Token);
        }

        private async Task MarkPendingUploadFailedAsync(string fileUuid, string reason)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var cleanupToken = cleanupTimeout.Token;
            await using var db = await _dbf.CreateDbContextAsync(cleanupToken);
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
                        cleanupToken);
                return;
            }

            var entry = await db.Uploads.SingleOrDefaultAsync(
                upload => upload.FileUuid == fileUuid && upload.Status == "Pending",
                cleanupToken);
            if (entry is null)
                return;

            entry.Status = "Failed";
            entry.LastError = boundedReason;
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cleanupToken);
        }

        private static string GetAuthenticatedRfc(ClaimsPrincipal? user)
        {
            var rfc =
                user?.FindFirst("rfc")?.Value ??
                user?.FindFirst(ClaimsIdentity.DefaultNameClaimType)?.Value ??
                user?.FindFirst(ClaimTypes.Name)?.Value;
            if (!IdentityInputPolicy.TryNormalizeRfc(rfc, out var normalized))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "El token no contiene un RFC válido."));
            return normalized;
        }

        private static string? GetAuthenticatedDevice(ClaimsPrincipal? user) =>
            user?.FindFirst("device")?.Value ??
            user?.FindFirst("device_id")?.Value ??
            user?.FindFirst("deviceId")?.Value;

        private static bool IsSha256(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length == 64 &&
            value.All(Uri.IsHexDigit);

        private static string CurrentTraceId(string? correlationId)
        {
            var activityTrace = Activity.Current?.TraceId.ToString();
            if (!string.IsNullOrWhiteSpace(activityTrace) && activityTrace != default(ActivityTraceId).ToString())
                return activityTrace;
            return Guid.TryParse(correlationId, out var parsed)
                ? parsed.ToString("N")
                : Guid.NewGuid().ToString("N");
        }

        private static string BuildStorageKey(UploadEntry entry)
        {
            var leafId = entry.IdRequerimiento ?? entry.IdResultado;
            if (entry.IdEnte is > 0 && entry.IdAuditoria is > 0 && entry.IdActo is > 0 &&
                entry.IdOficio is > 0 && leafId is > 0)
            {
                return $"{entry.IdEnte.Value}/{entry.IdAuditoria.Value}/{entry.IdActo.Value}/{entry.IdOficio.Value}/{leafId.Value}";
            }
            return entry.Rfc;
        }

        private static void EnsureResumeIdentity(
            UploadEntry entry,
            UploadStatusRequest request,
            string rfc,
            string? authenticatedDevice)
        {
            if (!string.Equals(entry.Rfc, rfc, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.OriginalFileName, request.OriginalFileName, StringComparison.Ordinal) ||
                entry.ExpectedLength != request.TotalLength ||
                !string.Equals(entry.ExpectedSha256, request.Sha256Hex, StringComparison.OrdinalIgnoreCase) ||
                entry.Periodo != request.Periodo ||
                entry.IdEnte != request.IdEnte ||
                entry.IdAuditoria != request.IdAuditoria ||
                entry.IdActo != request.IdActo ||
                entry.IdOficio != request.IdOficio ||
                entry.IdRequerimiento.GetValueOrDefault() != request.IdRequerimiento ||
                entry.IdResultado.GetValueOrDefault() != request.ResultadoId)
            {
                throw new RpcException(new Status(
                    StatusCode.PermissionDenied,
                    "La identidad, contenido o contexto no coincide con la carga parcial."));
            }

            if (!string.IsNullOrWhiteSpace(entry.DeviceId) &&
                !string.Equals(entry.DeviceId, authenticatedDevice, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "La carga parcial pertenece a otro dispositivo."));
            }
        }

        private static void ValidateFileName(string fileName)
        {
            if (!UploadFileNamePolicy.TryValidate(fileName, out var error))
                throw new RpcException(new Status(StatusCode.InvalidArgument, error));
        }

        private static async Task<bool> MoveNextWithTimeoutAsync(
            IAsyncStreamReader<UploadChunk> requestStream,
            TimeSpan timeout,
            CancellationToken uploadCancellationToken,
            CancellationToken callerCancellationToken)
        {
            using var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(uploadCancellationToken);
            chunkTimeout.CancelAfter(timeout);
            try
            {
                return await requestStream.MoveNext(chunkTimeout.Token);
            }
            catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
            {
                var detail = uploadCancellationToken.IsCancellationRequested
                    ? "La carga excedió su duración máxima."
                    : "La carga excedió el tiempo permitido sin enviar datos.";
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, detail));
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
