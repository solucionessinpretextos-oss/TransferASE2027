using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileXfer.FileSafety;
using FileXfer.Proto;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;

const string Rfc = "CARJ721209SD2";
const int Periodo = 2023;
const int IdEnte = 15104;
const int IdAuditoria = 5015;
const int IdActo = 1;
const int IdOficio = 27570;
const int IdRequerimiento = 24604;
const int ChunkSize = 1024 * 1024;

var repoRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
var serverDirectory = Path.Combine(repoRoot, "FileXfer.Server.Full");
var settings = TestSettings.Load(serverDirectory);
var qpdf = Path.Combine(repoRoot, "tools", "qpdf", "win-x64", "bin", "qpdf.exe");
Require(File.Exists(qpdf), $"No se encontró qpdf en {qpdf}.");

var recoveryOnly = args.Length >= 3 && args[1].Equals("--recover", StringComparison.OrdinalIgnoreCase);
var downloadOnly = args.Length >= 3 && args[1].Equals("--download", StringComparison.OrdinalIgnoreCase);
var runId = recoveryOnly
    ? args[2]
    : DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
var prefix = $"E2E_Juan_Carrillo_{runId}";
var root = Path.Combine(Path.GetTempPath(), "TransferASE-UploadE2E-" + runId);
var journalRoot = Path.Combine(root, "journal");
Directory.CreateDirectory(root);

var validPath = Path.Combine(root, prefix + "_valido.pdf");
var repairablePath = Path.Combine(root, prefix + "_reparable.pdf");
var encryptedPath = Path.Combine(root, prefix + "_cifrado.pdf");
var activePath = Path.Combine(root, prefix + "_activo.pdf");
var unsupportedPath = Path.Combine(root, prefix + "_bloqueado.exe");
var resumablePath = Path.Combine(root, prefix + "_reanudable.pdf");

try
{
    Console.WriteLine($"INFO identity=Juan Manuel Carrillo Rojo rfc={Rfc}");
    Console.WriteLine($"INFO context=periodo:{Periodo} ente:{IdEnte} auditoria:{IdAuditoria} acto:{IdActo} oficio:{IdOficio} requerimiento:{IdRequerimiento}");

    await using var fileXfer = new SqlConnection(settings.FileXferConnectionString);
    await fileXfer.OpenAsync();
    var user = await LoadUserAsync(fileXfer, Rfc);
    Require(user.IsActive, "El usuario de Juan está inactivo en FileXferDev.");
    Require(!user.MfaRequired, "El usuario de Juan todavía exige MFA en FileXferDev.");

    await using var budi = new SqlConnection(settings.BudiConnectionString);
    await budi.OpenAsync();
    await ValidateBusinessContextAsync(budi);
    var businessRowsBefore = await CountBusinessRowsAsync(budi, prefix + "%");
    if (!recoveryOnly)
        Require(businessRowsBefore == 0, "El identificador único de la prueba ya existe en BuDi_dev.");

    WriteSimplePdf(validPath, corruptStartXref: false, activeContent: false, contentBytes: 64 * 1024);
    WriteSimplePdf(repairablePath, corruptStartXref: true, activeContent: false, contentBytes: 32 * 1024);
    WriteSimplePdf(activePath, corruptStartXref: false, activeContent: true, contentBytes: 1024);
    WriteSimplePdf(resumablePath, corruptStartXref: false, activeContent: false, contentBytes: 12 * 1024 * 1024);
    await File.WriteAllBytesAsync(unsupportedPath, "MZ\0\0prueba-e2e"u8.ToArray());
    await WriteEncryptedPdfAsync(qpdf, validPath, encryptedPath);

    Environment.SetEnvironmentVariable("TRANSFER_ENVIRONMENT", "DESARROLLO");
    Environment.SetEnvironmentVariable("TRANSFER_API_URL", settings.ApiUrl);
    Environment.SetEnvironmentVariable("TRANSFER_GRPC_URL", settings.ApiUrl);
    Environment.SetEnvironmentVariable("FILEXFER_QPDF_PATH", qpdf);

    var deviceId = "E2E-JUAN-" + runId;
    var token = IssueToken(settings, user, deviceId);
    SesionAplicacion.Rfc = Rfc;
    SesionAplicacion.NombreUsuario = "Juan Manuel Carrillo Rojo";
    SesionAplicacion.DeviceId = deviceId;
    SesionAplicacion.TokenJwt = token;

    var configuration = new ServicioConfiguracionCliente();
    if (downloadOnly)
    {
        Require(int.TryParse(args[2], out var archivoLocalId) && archivoLocalId > 0,
            "El modo --download requiere un Id de archivo local válido.");
        await VerifyExistingDownloadAsync(
            configuration, token, archivoLocalId, fileXfer, budi);
        return 0;
    }

    var context = new ContextoRequerimiento
    {
        Periodo = Periodo,
        IdColabora = IdEnte,
        IdAuditoria = IdAuditoria.ToString(),
        IdActo = IdActo,
        IdOficio = IdOficio,
        IdRequerimiento = IdRequerimiento,
        Rfc = Rfc,
        EnteFiscalizado = "Municipio de Guasave",
        NumeroOficio = "ASE/PR1085/2023",
        DescripcionRequerimiento = "Control Interno"
    };

    var journal = new ServicioDiarioTransferencias(journalRoot);
    var uploader = new ServicioCargaArchivos(configuration, diario: journal);

    if (recoveryOnly)
    {
        Require(businessRowsBefore == 1,
            "La recuperación controlada esperaba exactamente un registro de catálogo previo.");
        var recoveryProgress = new InlineProgress<ProgresoCargaArchivo>();
        var recoveryResult = await uploader.EnviarArchivosAsync(
            context, new[] { validPath }, recoveryProgress, CancellationToken.None);
        Require(recoveryResult.ArchivosEnviados == 1 && recoveryResult.ArchivosReanudados == 1,
            "La finalización fallida no se pudo retomar desde los bytes ya conservados.");
        Require(recoveryResult.RecibosEmitidos.Count == 1,
            "La recuperación no emitió el recibo pendiente.");
        await VerifyPersistedUploadAsync(
            fileXfer, budi, settings.StorageBasePath, Path.GetFileName(validPath));
        Require(await CountUploadsAsync(fileXfer, Path.GetFileName(validPath)) == 1,
            "La recuperación duplicó la fila técnica.");
        Require(await CountBusinessRowsAsync(budi, prefix + "%") == businessRowsBefore,
            "La recuperación idempotente duplicó el catálogo de BuDi_dev.");
        Console.WriteLine("PASS recovery=catalog-failed reused-bytes=true duplicate-business-rows=0 receipt=issued");
        return 0;
    }

    var validProgress = new InlineProgress<ProgresoCargaArchivo>();
    var validResult = await uploader.EnviarArchivosAsync(
        context, new[] { validPath }, validProgress, CancellationToken.None);
    Require(validResult.ArchivosEnviados == 1 && validResult.PdfVerificados == 1,
        "La carga del PDF válido no reportó un archivo enviado y verificado.");
    Require(validResult.RecibosEmitidos.Count == 1, "La carga válida no emitió exactamente un recibo.");
    AssertProgress(validProgress.Items, expectRepairNotice: false);
    Console.WriteLine("PASS upload=valid-pdf verified=true progress=file-and-total receipt=issued");

    var duplicateProgress = new InlineProgress<ProgresoCargaArchivo>();
    var duplicateResult = await uploader.EnviarArchivosAsync(
        context, new[] { validPath }, duplicateProgress, CancellationToken.None);
    Require(duplicateResult.ArchivosEnviados == 1, "El reintento idempotente no fue reconocido como completado.");
    Require(duplicateResult.RecibosEmitidos.Single() == validResult.RecibosEmitidos.Single(),
        "El reintento idempotente produjo un recibo diferente.");
    Console.WriteLine("PASS retry=idempotent same-receipt=true");

    await SoftDeleteBusinessRowAsync(budi, Path.GetFileName(validPath));
    Require(await CountActiveBusinessRowsAsync(budi, Path.GetFileName(validPath)) == 0,
        "La preparación de la prueba no dio de baja el documento del catálogo.");
    var restoreProgress = new InlineProgress<ProgresoCargaArchivo>();
    var restoreResult = await uploader.EnviarArchivosAsync(
        context, new[] { validPath }, restoreProgress, CancellationToken.None);
    Require(restoreResult.ArchivosEnviados == 1 && restoreResult.ArchivosReanudados == 1,
        "La recarga de un documento dado de baja no reutilizó de forma segura la carga completada.");
    Require(restoreResult.RecibosEmitidos.Single() == validResult.RecibosEmitidos.Single(),
        "La reactivación idempotente cambió el recibo digital de la carga.");
    Require(await CountActiveBusinessRowsAsync(budi, Path.GetFileName(validPath)) == 1,
        "El documento recargado no volvió a quedar visible en el requerimiento.");
    Console.WriteLine("PASS retry=soft-deleted-business-row reactivated=true visible-in-requirement=true");

    var repairedProgress = new InlineProgress<ProgresoCargaArchivo>();
    var repairedResult = await uploader.EnviarArchivosAsync(
        context, new[] { repairablePath }, repairedProgress, CancellationToken.None);
    Require(repairedResult.ArchivosEnviados == 1 && repairedResult.PdfReparados == 1,
        "El PDF reparable no fue reparado, revalidado y enviado.");
    AssertProgress(repairedProgress.Items, expectRepairNotice: true);
    Console.WriteLine("PASS upload=damaged-pdf repaired=true revalidated=true uploaded=true user-informed=true");

    await ExpectRejectedAsync(uploader, context, encryptedPath, "contraseña", "cifrado");
    await ExpectRejectedAsync(uploader, context, activePath, "contenido activo", "inseguro");
    await ExpectRejectedAsync(uploader, context, unsupportedPath, "extensión", "no habilitada");
    Console.WriteLine("PASS reject=encrypted active-content unsupported-extension no-upload=true");

    var resumableHash = HashFile(resumablePath);
    var resumableUuid = CreateDeterministicUuid(
        Rfc, deviceId, Periodo, IdEnte, IdAuditoria, IdActo, IdOficio,
        IdRequerimiento, 0, Path.GetFileName(resumablePath), resumableHash);
    var partialOffset = await InterruptUploadAsync(
        configuration, token, deviceId, resumableUuid, resumablePath, resumableHash);
    Require(partialOffset > 0 && partialOffset < new FileInfo(resumablePath).Length,
        "El servidor no conservó un punto parcial reanudable.");

    var resumeProgress = new InlineProgress<ProgresoCargaArchivo>();
    var resumeResult = await uploader.EnviarArchivosAsync(
        context, new[] { resumablePath }, resumeProgress, CancellationToken.None);
    Require(resumeResult.ArchivosEnviados == 1 && resumeResult.ArchivosReanudados == 1,
        "La carga interrumpida no se reanudó desde el servidor.");
    Require(resumeProgress.Items.Any(item => item.Etapa == "Reanudación"),
        "No se informó al usuario que la carga estaba reanudándose.");
    Console.WriteLine($"PASS resume=network-interruption offset={partialOffset} completed=true user-informed=true");

    var successfulNames = new[]
    {
        Path.GetFileName(validPath),
        Path.GetFileName(repairablePath),
        Path.GetFileName(resumablePath)
    };
    foreach (var name in successfulNames)
        await VerifyPersistedUploadAsync(fileXfer, budi, settings.StorageBasePath, name);

    foreach (var rejectedName in new[]
             {
                 Path.GetFileName(encryptedPath),
                 Path.GetFileName(activePath),
                 Path.GetFileName(unsupportedPath)
             })
    {
        Require(await CountUploadsAsync(fileXfer, rejectedName) == 0,
            $"El archivo rechazado {rejectedName} alcanzó la tabla Uploads.");
    }

    Require(await CountUploadsAsync(fileXfer, Path.GetFileName(validPath)) == 1,
        "El reintento idempotente duplicó la fila técnica de la carga válida.");
    var businessRowsAfter = await CountBusinessRowsAsync(budi, prefix + "%");
    Require(businessRowsAfter == businessRowsBefore + successfulNames.Length,
        $"BuDi_dev esperaba {successfulNames.Length} registros nuevos y encontró {businessRowsAfter - businessRowsBefore}.");

    Console.WriteLine("PASS persistence=FileXferDev+BuDi_dev storage-hash=verified receipt-signature=verified context=verified");
    await SoftDeleteBusinessRowsAsync(budi, prefix + "%", successfulNames.Length);
    Console.WriteLine("PASS cleanup=business-test-rows hidden-from-client=true");
    Console.WriteLine($"RESULT run={runId} successful={successfulNames.Length} rejected=3 duplicateRows=0 businessRows={businessRowsAfter - businessRowsBefore}");
    return 0;
}
finally
{
    SesionAplicacion.Limpiar();
    Environment.SetEnvironmentVariable("FILEXFER_QPDF_PATH", null);
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task<UserRecord> LoadUserAsync(SqlConnection connection, string rfc)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT TOP (1) [DisplayName], [Roles], [IsActive], [MfaRequired], [SecurityVersion] FROM [dbo].[Users] WHERE [Rfc] = @rfc;";
    command.Parameters.AddWithValue("@rfc", rfc);
    await using var reader = await command.ExecuteReaderAsync();
    Require(await reader.ReadAsync(), $"No existe el RFC {rfc} en FileXferDev.dbo.Users.");
    var rolesRaw = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
    string[] roles;
    try { roles = JsonSerializer.Deserialize<string[]>(rolesRaw) ?? Array.Empty<string>(); }
    catch { roles = rolesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
    return new UserRecord(
        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
        roles.Length == 0 ? new[] { "user" } : roles,
        reader.GetBoolean(2),
        reader.GetBoolean(3),
        reader.GetInt32(4));
}

static async Task ValidateBusinessContextAsync(SqlConnection connection)
{
    await using (var office = connection.CreateCommand())
    {
        office.CommandText = """
            SELECT COUNT_BIG(1)
            FROM [dbo].[BUZ_Movimientos]
            WHERE [ID_Movimiento] = @oficio
              AND [ID_Auditoria] = @auditoria
              AND [ID_Acto] = @acto;
            """;
        office.Parameters.AddWithValue("@oficio", IdOficio);
        office.Parameters.AddWithValue("@auditoria", IdAuditoria);
        office.Parameters.AddWithValue("@acto", IdActo);
        Require(Convert.ToInt64(await office.ExecuteScalarAsync()) == 1,
            "El oficio seleccionado ya no coincide con la auditoría y el acto en BuDi_dev.");
    }

    await using var requirements = connection.CreateCommand();
    requirements.CommandText = "PA_Consulta_RequerimientosByIdOficioV2";
    requirements.CommandType = System.Data.CommandType.StoredProcedure;
    requirements.Parameters.AddWithValue("@IdOficio", IdOficio);
    requirements.Parameters.AddWithValue("@IdColabora", IdEnte);
    requirements.Parameters.AddWithValue("@Skip", 0);
    requirements.Parameters.AddWithValue("@Take", 10_000);
    requirements.Parameters.AddWithValue("@Search", DBNull.Value);
    await using var reader = await requirements.ExecuteReaderAsync();
    var idOrdinal = Enumerable.Range(0, reader.FieldCount)
        .FirstOrDefault(index =>
            reader.GetName(index).Equals("IdRequerimiento", StringComparison.OrdinalIgnoreCase) ||
            reader.GetName(index).Equals("ID_Requerimiento", StringComparison.OrdinalIgnoreCase));
    var found = false;
    while (await reader.ReadAsync())
        found |= Convert.ToInt32(reader.GetValue(idOrdinal)) == IdRequerimiento;
    Require(found, "El requerimiento seleccionado ya no está asignado a Juan en ese oficio.");
}

static string IssueToken(TestSettings settings, UserRecord user, string deviceId)
{
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSigningKey));
    var claims = new List<Claim>
    {
        new("rfc", Rfc),
        new(ClaimTypes.NameIdentifier, Rfc),
        new("securityVersion", user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("amr", "e2e-development"),
        new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        new("deviceId", deviceId)
    };
    claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
    var jwt = new JwtSecurityToken(
        settings.JwtIssuer,
        settings.JwtAudience,
        claims,
        DateTime.UtcNow.AddMinutes(-1),
        DateTime.UtcNow.AddMinutes(15),
        new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    return new JwtSecurityTokenHandler().WriteToken(jwt);
}

static async Task ExpectRejectedAsync(
    ServicioCargaArchivos uploader,
    ContextoRequerimiento context,
    string path,
    params string[] expectedFragments)
{
    var progress = new InlineProgress<ProgresoCargaArchivo>();
    try
    {
        await uploader.EnviarArchivosAsync(context, new[] { path }, progress, CancellationToken.None);
        throw new InvalidOperationException($"{Path.GetFileName(path)} fue aceptado cuando debía rechazarse.");
    }
    catch (Exception exception) when (exception is FileValidationException or InvalidOperationException)
    {
        var combined = exception.Message + " " + string.Join(" ", progress.Items.Select(item => item.Mensaje));
        Require(expectedFragments.Any(fragment => combined.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            $"El rechazo de {Path.GetFileName(path)} no explicó la causa esperada. Detalle: {exception.Message}");
    }
}

static async Task<long> InterruptUploadAsync(
    ServicioConfiguracionCliente configuration,
    string token,
    string deviceId,
    string fileUuid,
    string path,
    string sha256)
{
    using var handler = configuration.CrearManejadorHttpSeguro();
    using var channel = GrpcChannel.ForAddress(configuration.DireccionGrpc, new GrpcChannelOptions { HttpHandler = handler });
    var client = new Upload.UploadClient(channel);
    var headers = new Metadata { { "Authorization", $"Bearer {token}" } };
    var total = new FileInfo(path).Length;
    using var cancellation = new CancellationTokenSource();
    using var call = client.UploadStream(headers, cancellationToken: cancellation.Token);
    await using var stream = File.OpenRead(path);
    var buffer = new byte[ChunkSize];
    var correlationId = Guid.NewGuid().ToString("D");
    long offset = 0;
    for (var chunkIndex = 0; chunkIndex < 3; chunkIndex++)
    {
        var read = await stream.ReadAsync(buffer);
        Require(read > 0, "El PDF reanudable es demasiado pequeño para simular una interrupción.");
        await call.RequestStream.WriteAsync(CreateChunk(
            fileUuid, Path.GetFileName(path), offset, ByteString.CopyFrom(buffer, 0, read),
            sha256, total, token, deviceId, correlationId));
        offset += read;
    }

    cancellation.Cancel();
    try { await call.ResponseAsync; }
    catch (RpcException exception) when (exception.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable) { }

    var request = CreateStatusRequest(fileUuid, Path.GetFileName(path), total, sha256, deviceId);
    for (var attempt = 0; attempt < 30; attempt++)
    {
        await Task.Delay(250);
        try
        {
            var status = await client.GetUploadStatusAsync(request, headers, deadline: DateTime.UtcNow.AddSeconds(5));
            if (status.ResumeAllowed && status.NextOffset > 0)
                return status.NextOffset;
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) { }
    }

    throw new InvalidOperationException("El servidor no publicó el punto de reanudación después de interrumpir la conexión.");
}

static UploadChunk CreateChunk(
    string fileUuid,
    string fileName,
    long offset,
    ByteString data,
    string sha256,
    long total,
    string token,
    string deviceId,
    string correlationId) => new()
{
    FileUuid = fileUuid,
    OriginalFileName = fileName,
    Offset = offset,
    Data = data,
    Sha256Hex = sha256,
    TotalLength = total,
    CorrelationId = correlationId,
    JwtToken = token,
    Rfc = Rfc,
    DeviceId = deviceId,
    Periodo = Periodo,
    IdEnte = IdEnte,
    IdAuditoria = IdAuditoria,
    IdActo = IdActo,
    IdOficio = IdOficio,
    IdRequerimiento = IdRequerimiento
};

static UploadStatusRequest CreateStatusRequest(
    string fileUuid,
    string fileName,
    long total,
    string sha256,
    string deviceId) => new()
{
    FileUuid = fileUuid,
    OriginalFileName = fileName,
    TotalLength = total,
    Sha256Hex = sha256,
    DeviceId = deviceId,
    Periodo = Periodo,
    IdEnte = IdEnte,
    IdAuditoria = IdAuditoria,
    IdActo = IdActo,
    IdOficio = IdOficio,
    IdRequerimiento = IdRequerimiento,
    CorrelationId = Guid.NewGuid().ToString("D")
};

static async Task VerifyPersistedUploadAsync(
    SqlConnection fileXfer,
    SqlConnection budi,
    string storageBasePath,
    string fileName)
{
    await using var command = fileXfer.CreateCommand();
    command.CommandText = """
        SELECT TOP (1)
            [Id], [StoredRelativePath], [BytesReceived], [ExpectedLength], [ExpectedSha256],
            [Sha256Server], [Sha256Client], [Status], [ReceiptId], [VerificationRequired],
            [VerificationPerformed], [VerificationOutcome], [Rfc], [Periodo], [IdEnte],
            [IdAuditoria], [IdActo], [IdOficio], [IdRequerimiento]
        FROM [dbo].[Uploads]
        WHERE [Rfc] = @rfc AND [OriginalFileName] = @name
        ORDER BY [CreatedUtc] DESC;
        """;
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@name", fileName);
    await using var reader = await command.ExecuteReaderAsync();
    Require(await reader.ReadAsync(), $"No se encontró la carga técnica de {fileName}.");
    var uploadId = reader.GetGuid(0);
    var relativePath = reader.GetString(1);
    var bytes = reader.GetInt64(2);
    var expected = reader.GetInt64(3);
    var expectedHash = reader.GetString(4);
    var serverHash = reader.GetString(5);
    var clientHash = reader.GetString(6);
    var status = reader.GetString(7);
    var receiptId = reader.GetGuid(8);
    var verificationRequired = reader.GetBoolean(9);
    var verificationPerformed = reader.GetBoolean(10);
    var verificationOutcome = reader.GetString(11);
    var persistedRfc = reader.GetString(12);
    var periodo = reader.GetInt32(13);
    var idEnte = reader.GetInt32(14);
    var idAuditoria = reader.GetInt32(15);
    var idActo = reader.GetInt32(16);
    var idOficio = reader.GetInt32(17);
    var idRequerimiento = reader.GetInt32(18);
    await reader.DisposeAsync();

    Require(status == "Completed", $"{fileName} terminó con estado {status}.");
    Require(bytes == expected && bytes > 0, $"La longitud persistida de {fileName} es inconsistente.");
    Require(expectedHash == serverHash && serverHash == clientHash, $"El SHA-256 persistido de {fileName} no coincide.");
    Require(verificationRequired && verificationPerformed && verificationOutcome == "Valid",
        $"La verificación profunda de {fileName} no terminó como válida.");
    Require(persistedRfc == Rfc && periodo == Periodo && idEnte == IdEnte && idAuditoria == IdAuditoria &&
            idActo == IdActo && idOficio == IdOficio && idRequerimiento == IdRequerimiento,
        $"El contexto persistido de {fileName} no corresponde a Juan/requerimiento esperado.");

    var physicalPath = Path.GetFullPath(Path.Combine(storageBasePath, relativePath));
    var storageRoot = Path.GetFullPath(storageBasePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    Require(physicalPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase),
        $"La ruta física de {fileName} escapó del almacenamiento configurado.");
    Require(File.Exists(physicalPath), $"El archivo físico de {fileName} no existe.");
    Require(HashFile(physicalPath) == serverHash, $"El archivo físico de {fileName} no conserva el SHA-256 registrado.");

    await VerifyReceiptAsync(fileXfer, receiptId, uploadId);

    await using var business = budi.CreateCommand();
    business.CommandText = """
        SELECT COUNT_BIG(1)
        FROM [dbo].[Transfer_ArchivosLocales]
        WHERE [ID_Upload] = @uploadId AND [RFC_Origen] = @rfc
          AND [RequerimientoId] = @req AND [ColaboraId] = @ente
          AND [Nombre_Archivo] = @name AND [Estatus] = 'A' AND [Hash] = @hash8;
        """;
    business.Parameters.AddWithValue("@uploadId", uploadId);
    business.Parameters.AddWithValue("@rfc", Rfc);
    business.Parameters.AddWithValue("@req", IdRequerimiento);
    business.Parameters.AddWithValue("@ente", IdEnte);
    business.Parameters.AddWithValue("@name", fileName);
    business.Parameters.AddWithValue("@hash8", serverHash[..8]);
    Require(Convert.ToInt64(await business.ExecuteScalarAsync()) == 1,
        $"BuDi_dev no contiene exactamente un registro activo para {fileName}.");
}

static async Task VerifyReceiptAsync(SqlConnection connection, Guid receiptId, Guid uploadId)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT [CanonicalPayloadJson], [PayloadSha256], [PreviousChainHash], [ChainHash],
               [SignatureBase64], [SignatureAlgorithm], [PublicKeyPem], [UploadId]
        FROM [dbo].[UploadReceipts] WHERE [Id] = @id;
        """;
    command.Parameters.AddWithValue("@id", receiptId);
    await using var reader = await command.ExecuteReaderAsync();
    Require(await reader.ReadAsync(), $"No existe el recibo {receiptId:D}.");
    var payload = reader.GetString(0);
    var payloadHash = reader.GetString(1);
    var previousHash = reader.IsDBNull(2) ? null : reader.GetString(2);
    var chainHash = reader.GetString(3);
    var signature = reader.GetString(4);
    var algorithm = reader.GetString(5);
    var publicKey = reader.GetString(6);
    var persistedUploadId = reader.GetGuid(7);
    Require(persistedUploadId == uploadId && algorithm == "RSA-PSS-SHA256", "El recibo no corresponde a la carga o al algoritmo esperado.");

    var computedPayloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    Require(Convert.ToHexString(computedPayloadHash).Equals(payloadHash, StringComparison.OrdinalIgnoreCase),
        "La huella del contenido canónico del recibo es inválida.");
    var previousBytes = string.IsNullOrWhiteSpace(previousHash) ? new byte[32] : Convert.FromHexString(previousHash);
    var chainMaterial = new byte[previousBytes.Length + computedPayloadHash.Length];
    previousBytes.CopyTo(chainMaterial, 0);
    computedPayloadHash.CopyTo(chainMaterial, previousBytes.Length);
    var computedChainHash = SHA256.HashData(chainMaterial);
    Require(Convert.ToHexString(computedChainHash).Equals(chainHash, StringComparison.OrdinalIgnoreCase),
        "La cadena criptográfica del recibo es inválida.");
    using var rsa = RSA.Create();
    rsa.ImportFromPem(publicKey);
    Require(rsa.VerifyHash(computedChainHash, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
        "La firma digital del recibo es inválida.");
}

static async Task<long> CountUploadsAsync(SqlConnection connection, string fileName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT_BIG(1) FROM [dbo].[Uploads] WHERE [Rfc] = @rfc AND [OriginalFileName] = @name;";
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@name", fileName);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<long> CountBusinessRowsAsync(SqlConnection connection, string pattern)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT_BIG(1) FROM [dbo].[Transfer_ArchivosLocales] WHERE [RFC_Origen] = @rfc AND [Nombre_Archivo] LIKE @pattern;";
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@pattern", pattern);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task SoftDeleteBusinessRowAsync(SqlConnection connection, string fileName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE [dbo].[Transfer_ArchivosLocales]
           SET [Estatus] = 'B'
         WHERE [RFC_Origen] = @rfc
           AND [RequerimientoId] = @req
           AND [ColaboraId] = @ente
           AND [Nombre_Archivo] = @name
           AND [Estatus] = 'A';
        SELECT @@ROWCOUNT;
        """;
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@req", IdRequerimiento);
    command.Parameters.AddWithValue("@ente", IdEnte);
    command.Parameters.AddWithValue("@name", fileName);
    Require(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1,
        $"No se pudo preparar la baja lógica controlada de {fileName}.");
}

static async Task<long> CountActiveBusinessRowsAsync(SqlConnection connection, string fileName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT_BIG(1)
          FROM [dbo].[Transfer_ArchivosLocales]
         WHERE [RFC_Origen] = @rfc
           AND [RequerimientoId] = @req
           AND [ColaboraId] = @ente
           AND [Nombre_Archivo] = @name
           AND [Estatus] = 'A';
        """;
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@req", IdRequerimiento);
    command.Parameters.AddWithValue("@ente", IdEnte);
    command.Parameters.AddWithValue("@name", fileName);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task SoftDeleteBusinessRowsAsync(
    SqlConnection connection,
    string pattern,
    int expectedRows)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE [dbo].[Transfer_ArchivosLocales]
           SET [Estatus] = 'B'
         WHERE [RFC_Origen] = @rfc
           AND [RequerimientoId] = @req
           AND [ColaboraId] = @ente
           AND [Nombre_Archivo] LIKE @pattern
           AND [Estatus] = 'A';
        SELECT @@ROWCOUNT;
        """;
    command.Parameters.AddWithValue("@rfc", Rfc);
    command.Parameters.AddWithValue("@req", IdRequerimiento);
    command.Parameters.AddWithValue("@ente", IdEnte);
    command.Parameters.AddWithValue("@pattern", pattern);
    Require(Convert.ToInt32(await command.ExecuteScalarAsync()) == expectedRows,
        "La limpieza controlada no ocultó exactamente los documentos generados por la prueba.");
}

static async Task VerifyExistingDownloadAsync(
    ServicioConfiguracionCliente configuration,
    string token,
    int archivoLocalId,
    SqlConnection fileXfer,
    SqlConnection budi)
{
    Guid uploadId;
    string fileName;
    await using (var business = budi.CreateCommand())
    {
        business.CommandText = """
            SELECT TOP (1) [ID_Upload], [Nombre_Archivo]
            FROM [dbo].[Transfer_ArchivosLocales]
            WHERE [Id] = @id AND [RFC_Origen] = @rfc AND [Estatus] = 'A';
            """;
        business.Parameters.AddWithValue("@id", archivoLocalId);
        business.Parameters.AddWithValue("@rfc", Rfc);
        await using var reader = await business.ExecuteReaderAsync();
        Require(await reader.ReadAsync() && !reader.IsDBNull(0),
            $"El archivo local {archivoLocalId} no tiene ID_Upload activo para Juan.");
        uploadId = reader.GetGuid(0);
        fileName = reader.GetString(1);
    }

    string expectedHash;
    long expectedLength;
    await using (var technical = fileXfer.CreateCommand())
    {
        technical.CommandText = """
            SELECT TOP (1) [Sha256Server], COALESCE([ExpectedLength], [BytesReceived])
            FROM [dbo].[Uploads]
            WHERE [Id] = @id AND [Rfc] = @rfc AND [Status] = 'Completed';
            """;
        technical.Parameters.AddWithValue("@id", uploadId);
        technical.Parameters.AddWithValue("@rfc", Rfc);
        await using var reader = await technical.ExecuteReaderAsync();
        Require(await reader.ReadAsync() && !reader.IsDBNull(0),
            $"Uploads no contiene evidencia completa para {uploadId:D}.");
        expectedHash = reader.GetString(0);
        expectedLength = reader.GetInt64(1);
    }

    using var handler = configuration.CrearManejadorHttpSeguro();
    using var http = new HttpClient(handler)
    {
        BaseAddress = new Uri(configuration.UrlBaseServidor.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    using var response = await http.GetAsync(
        $"archivos-locales/{archivoLocalId}/download",
        HttpCompletionOption.ResponseHeadersRead);
    var body = response.IsSuccessStatusCode
        ? string.Empty
        : await response.Content.ReadAsStringAsync();
    Require(response.IsSuccessStatusCode,
        $"La descarga real devolvió {(int)response.StatusCode}: {body}");
    Require(response.Headers.TryGetValues("X-Content-SHA256", out var values),
        "La descarga no incluyó X-Content-SHA256.");
    var responseHash = values?.SingleOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;
    Require(responseHash == expectedHash,
        "La huella anunciada por la descarga no coincide con Uploads.");

    await using var source = await response.Content.ReadAsStreamAsync();
    using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[128 * 1024];
    long length = 0;
    while (true)
    {
        var read = await source.ReadAsync(buffer);
        if (read == 0) break;
        incremental.AppendData(buffer, 0, read);
        length += read;
    }
    var actualHash = Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    Require(length == expectedLength && actualHash == expectedHash,
        "Los bytes descargados no coinciden con la longitud y SHA-256 registrados.");
    Console.WriteLine(
        $"PASS actual-download id={archivoLocalId} upload={uploadId:D} file={fileName} bytes={length} sha256=verified");
}

static void AssertProgress(IReadOnlyList<ProgresoCargaArchivo> items, bool expectRepairNotice)
{
    Require(items.Count > 0, "No se publicaron eventos de avance.");
    Require(items.Select(item => item.PorcentajeArchivo).SequenceEqual(items.Select(item => item.PorcentajeArchivo).OrderBy(value => value)),
        "El porcentaje del archivo retrocedió.");
    Require(items.Select(item => item.PorcentajeTotal).SequenceEqual(items.Select(item => item.PorcentajeTotal).OrderBy(value => value)),
        "El porcentaje total retrocedió.");
    Require(items.Any(item => item.PorcentajeArchivo >= 100 && item.PorcentajeTotal >= 100),
        "El progreso no alcanzó 100%.");
    if (expectRepairNotice)
    {
        Require(items.Any(item => item.Mensaje.Contains("repar", StringComparison.OrdinalIgnoreCase)),
            "La reparación no fue informada al usuario.");
    }
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string CreateDeterministicUuid(
    string rfc, string deviceId, int periodo, int idEnte, int idAuditoria,
    int idActo, int idOficio, int idRequerimiento, int idResultado,
    string fileName, string sha256)
{
    var canonical = string.Join("|", rfc.Trim().ToUpperInvariant(), deviceId.Trim(),
        periodo, idEnte, idAuditoria, idActo, idOficio, idRequerimiento, idResultado,
        fileName.Trim().ToUpperInvariant(), sha256);
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    return new Guid(digest.AsSpan(0, 16)).ToString("N");
}

static async Task WriteEncryptedPdfAsync(string qpdf, string source, string destination)
{
    var start = new ProcessStartInfo
    {
        FileName = qpdf,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[] { "--encrypt", "e2e-user", "e2e-owner", "256", "--", source, destination })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar qpdf.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Require(process.ExitCode == 0 && File.Exists(destination),
        $"qpdf no generó el PDF cifrado: {(await stdout)} {(await stderr)}");
}

static void WriteSimplePdf(string path, bool corruptStartXref, bool activeContent, int contentBytes)
{
    var bytes = new List<byte>(contentBytes + 2048);
    AppendAscii(bytes, "%PDF-1.4\n%1234\n");
    var objects = new List<Action>
    {
        () => AppendAscii(bytes, activeContent
            ? "<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>"
            : "<< /Type /Catalog /Pages 2 0 R >>"),
        () => AppendAscii(bytes, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        () => AppendAscii(bytes, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>"),
        () =>
        {
            AppendAscii(bytes, $"<< /Length {contentBytes} >>\nstream\n");
            bytes.AddRange(Enumerable.Repeat((byte)' ', contentBytes));
            AppendAscii(bytes, "\nendstream");
        }
    };
    if (activeContent)
        objects.Add(() => AppendAscii(bytes, "<< /Type /Action /S /JavaScript /JS (app.alert\\(test\\)) >>"));

    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Count; index++)
    {
        offsets.Add(bytes.Count);
        AppendAscii(bytes, $"{index + 1} 0 obj\n");
        objects[index]();
        AppendAscii(bytes, "\nendobj\n");
    }
    var xrefOffset = bytes.Count;
    AppendAscii(bytes, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    for (var index = 1; index < offsets.Count; index++)
        AppendAscii(bytes, $"{offsets[index]:D10} 00000 n \n");
    AppendAscii(bytes, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{(corruptStartXref ? xrefOffset + 17 : xrefOffset)}\n%%EOF\n");
    File.WriteAllBytes(path, bytes.ToArray());
}

static void AppendAscii(List<byte> target, string value) => target.AddRange(Encoding.ASCII.GetBytes(value));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed record UserRecord(
    string DisplayName,
    string[] Roles,
    bool IsActive,
    bool MfaRequired,
    int SecurityVersion);

file sealed class InlineProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = new();
    public void Report(T value) => Items.Add(value);
}

file sealed record TestSettings(
    string ApiUrl,
    string FileXferConnectionString,
    string BudiConnectionString,
    string StorageBasePath,
    string JwtIssuer,
    string JwtAudience,
    string JwtSigningKey)
{
    public static TestSettings Load(string serverDirectory)
    {
        var common = Read(Path.Combine(serverDirectory, "appsettings.json"));
        var development = Read(Path.Combine(serverDirectory, "appsettings.Development.json"));
        var secretsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", "FileXfer.Server.2027", "secrets.json");
        var secrets = Read(secretsPath, required: false);

        string Resolve(string key, params JsonElement[] sources)
        {
            foreach (var source in sources)
            {
                var value = Get(source, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            throw new InvalidOperationException($"Falta la configuración {key} para la prueba E2E.");
        }

        return new TestSettings(
            Environment.GetEnvironmentVariable("TRANSFER_API_URL") ?? "https://localhost:51136",
            Resolve("ConnectionStrings:FileXferDb", secrets, development, common),
            Resolve("ConnectionStrings:BudiDev", secrets, development, common),
            Resolve("Storage:BasePath", development, common),
            Resolve("Security:Jwt:Issuer", development, common),
            Resolve("Security:Jwt:Audience", development, common),
            Resolve("Security:Jwt:SigningKey", secrets, development, common));
    }

    private static JsonElement Read(string path, bool required = true)
    {
        if (!File.Exists(path))
        {
            if (required) throw new FileNotFoundException("No se encontró la configuración de prueba.", path);
            return default;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string? Get(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty(key, out var flat)) return flat.ValueKind == JsonValueKind.String ? flat.GetString() : flat.ToString();
        var current = root;
        foreach (var segment in key.Split(':'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }
}
