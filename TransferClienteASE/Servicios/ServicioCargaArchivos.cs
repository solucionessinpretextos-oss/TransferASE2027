// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioCargaArchivos.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Inspecciona, identifica y transmite archivos mediante gRPC reanudable.
// Regla de mantenimiento: La política efectiva siempre proviene del servidor; nunca confiar
// exclusivamente en el filtro visual del cliente para autorizar una extensión.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.FileSafety;
using FileXfer.Proto;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using TransferClienteASE.Modelos;

namespace TransferClienteASE.Servicios
{
    public sealed record PoliticaArchivoEfectiva(
        string Extension,
        bool IsAllowed,
        bool RequiresVerification,
        long? MaxFileBytes,
        int Version);

    public sealed record ProgresoCargaArchivo(
        string RutaOriginal,
        double PorcentajeArchivo,
        double PorcentajeTotal,
        int NumeroArchivo,
        int TotalArchivos,
        string Etapa,
        string Mensaje,
        string? ReciboId = null,
        string? TraceId = null)
    {
        public double Porcentaje => PorcentajeArchivo;
    }

    public sealed record ResultadoCargaArchivos(
        int ArchivosEnviados,
        int ArchivosVerificados,
        int ArchivosReparados,
        int PdfVerificados,
        int PdfReparados,
        int VerificacionesOmitidas,
        int ArchivosReanudados,
        IReadOnlyList<string> RecibosEmitidos);

    public sealed class ServicioCargaArchivos
    {
        private const int TamanoBloque = 1024 * 1024;
        private const int MaximoReintentos = 4;
        private readonly ServicioConfiguracionCliente _configuracion;
        private readonly IFileSafetyService _fileSafety;
        private readonly ServicioDiarioTransferencias _diario;

        public ServicioCargaArchivos(
            ServicioConfiguracionCliente configuracion,
            IFileSafetyService? fileSafety = null,
            ServicioDiarioTransferencias? diario = null)
        {
            _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
            _fileSafety = fileSafety ?? new DefaultFileSafetyService();
            _diario = diario ?? new ServicioDiarioTransferencias();
        }

        public async Task<IReadOnlyList<PoliticaArchivoEfectiva>> ObtenerPoliticasEfectivasAsync(
            CancellationToken ct = default)
        {
            if (!SesionAplicacion.EstaAutenticado || string.IsNullOrWhiteSpace(SesionAplicacion.TokenJwt))
                throw new InvalidOperationException("No hay sesión autenticada para consultar las políticas de archivo.");

            using var handler = _configuracion.CrearManejadorHttpSeguro();
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(_configuracion.UrlBaseServidor, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(30)
            };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SesionAplicacion.TokenJwt);

            using var response = await http.GetAsync("file-policies/effective", ct);
            response.EnsureSuccessStatusCode();
            var policies = await response.Content.ReadFromJsonAsync<List<PoliticaArchivoEfectiva>>(
                cancellationToken: ct);
            return policies is null ? Array.Empty<PoliticaArchivoEfectiva>() : policies;
        }

        public async Task<ResultadoCargaArchivos> EnviarArchivosAsync(
            ContextoRequerimiento contexto,
            IReadOnlyList<string> rutasArchivos,
            IProgress<ProgresoCargaArchivo> progreso,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(contexto);
            ArgumentNullException.ThrowIfNull(rutasArchivos);
            ArgumentNullException.ThrowIfNull(progreso);

            if (!SesionAplicacion.EstaAutenticado)
                throw new InvalidOperationException("No hay sesión autenticada para realizar la subida.");

            var rfc = SesionAplicacion.Rfc;
            var jwtToken = SesionAplicacion.TokenJwt;
            var deviceId = SesionAplicacion.DeviceId;
            if (string.IsNullOrWhiteSpace(jwtToken))
                throw new InvalidOperationException("No se encontró TokenJwt en la sesión de aplicación.");

            var idEnte = contexto.Ente?.Id ?? 0;
            var idAuditoria = contexto.Auditoria?.Id ?? 0;
            var idActo = contexto.Acto?.Id ?? 0;
            var idOficio = contexto.Oficio?.Id ?? 0;
            var idResultado = contexto.ResultadoId > 0 ? contexto.ResultadoId : contexto.IdResultado;
            var idRequerimiento = contexto.Requerimiento?.Id ?? 0;
            if (idEnte <= 0 || idAuditoria <= 0 || idActo <= 0 || idOficio <= 0 ||
                (idResultado <= 0 && idRequerimiento <= 0))
            {
                throw new InvalidOperationException(
                    $"ContextoRequerimiento incompleto. IdEnte={idEnte}, IdAuditoria={idAuditoria}, " +
                    $"IdActo={idActo}, IdOficio={idOficio}, IdRequerimiento={idRequerimiento}, ResultadoId={idResultado}");
            }

            var rutasParaEnviar = rutasArchivos
                .Where(ruta => !string.IsNullOrWhiteSpace(ruta))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (rutasParaEnviar.Length == 0)
                return new ResultadoCargaArchivos(0, 0, 0, 0, 0, 0, 0, Array.Empty<string>());

            progreso.Report(new ProgresoCargaArchivo(
                string.Empty, 0, 0, 0, rutasParaEnviar.Length,
                "Políticas", "Consultando extensiones y verificaciones autorizadas por el servidor..."));
            var policies = (await ObtenerPoliticasEfectivasAsync(ct))
                .ToDictionary(p => NormalizarExtension(p.Extension), StringComparer.OrdinalIgnoreCase);
            if (policies.Count == 0)
                throw new InvalidOperationException("El servidor no publicó ninguna extensión de archivo habilitada.");

            using var grpcHandler = _configuracion.CrearManejadorHttpSeguro();
            using var channel = GrpcChannel.ForAddress(_configuracion.DireccionGrpc, new GrpcChannelOptions
            {
                HttpHandler = grpcHandler
            });
            var clienteGrpc = new Upload.UploadClient(channel);
            var headers = new Metadata { { "Authorization", $"Bearer {jwtToken}" } };

            var archivosEnviados = 0;
            var archivosVerificados = 0;
            var archivosReparados = 0;
            var pdfVerificados = 0;
            var pdfReparados = 0;
            var verificacionesOmitidas = 0;
            var archivosReanudados = 0;
            var recibos = new List<string>();
            var stagingArchivos = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASE", "TransferClienteASE", "FilePreflight");

            for (var indiceArchivo = 0; indiceArchivo < rutasParaEnviar.Length; indiceArchivo++)
            {
                ct.ThrowIfCancellationRequested();
                var ruta = rutasParaEnviar[indiceArchivo];
                var numeroArchivo = indiceArchivo + 1;
                var totalArchivos = rutasParaEnviar.Length;
                var nombreArchivo = Path.GetFileName(ruta);
                var ultimoPorcentajeReportado = 0d;

                void Reportar(
                    double porcentajeArchivo,
                    string etapa,
                    string mensaje,
                    string? reciboId = null,
                    string? traceId = null)
                {
                    var porcentajeAcotado = Math.Max(
                        ultimoPorcentajeReportado,
                        Math.Clamp(porcentajeArchivo, 0, 100));
                    ultimoPorcentajeReportado = porcentajeAcotado;
                    var porcentajeTotal = ((indiceArchivo + porcentajeAcotado / 100d) / totalArchivos) * 100d;
                    progreso.Report(new ProgresoCargaArchivo(
                        ruta, porcentajeAcotado, Math.Clamp(porcentajeTotal, 0, 100),
                        numeroArchivo, totalArchivos, etapa,
                        $"[{numeroArchivo}/{totalArchivos}] {mensaje}", reciboId, traceId));
                }

                Reportar(0, "Preparación", $"Preparando {nombreArchivo}...");
                var entradaDiario = await _diario.ObtenerAsync(contexto, ruta, ct);
                if (entradaDiario is null && File.Exists(ruta))
                {
                    await _diario.RegistrarEnColaAsync(contexto, ruta, ct);
                    entradaDiario = await _diario.ObtenerAsync(contexto, ruta, ct);
                }

                var existeCopiaRecuperable =
                    !string.IsNullOrWhiteSpace(entradaDiario?.PreparedPath) &&
                    File.Exists(entradaDiario.PreparedPath);
                if (!File.Exists(ruta) && !existeCopiaRecuperable)
                {
                    var mensajeFaltante =
                        $"No se encontró {nombreArchivo} ni su copia segura de recuperación. " +
                        "Seleccione nuevamente el archivo para continuar.";
                    Reportar(0, "Esperando archivo", mensajeFaltante);
                    await _diario.MarcarProgresoAsync(
                        contexto, ruta, entradaDiario?.ConfirmedBytes ?? 0,
                        "WaitingForSource", mensajeFaltante, ct);
                    throw new FileNotFoundException(mensajeFaltante, ruta);
                }

                var extension = NormalizarExtension(Path.GetExtension(nombreArchivo));
                if (!policies.TryGetValue(extension, out var policy) || !policy.IsAllowed)
                {
                    throw new FileValidationException(
                        FileInspectionOutcome.Unsupported,
                        $"La extensión {extension} no está habilitada actualmente por el servidor.");
                }

                var originalLength = File.Exists(ruta)
                    ? new FileInfo(ruta).Length
                    : entradaDiario?.OriginalLength ?? 0;
                ValidarLimitePolitica(policy, originalLength, nombreArchivo);
                PreparedSafeFile? archivoPreparado = null;
                var offsetConfirmadoDiario = entradaDiario?.ConfirmedBytes ?? 0;

                try
                {
                    string rutaEnvio = string.Empty;
                    string sha256Hex = string.Empty;
                    string fileUuid = string.Empty;
                    long totalLength = 0;
                    var fueReparado = false;
                    var reutilizoCopia = false;

                    if (existeCopiaRecuperable &&
                        !string.IsNullOrWhiteSpace(entradaDiario?.Sha256) &&
                        !string.IsNullOrWhiteSpace(entradaDiario.FileUuid) &&
                        entradaDiario.VerificationRequired == policy.RequiresVerification)
                    {
                        rutaEnvio = entradaDiario.PreparedPath!;
                        totalLength = new FileInfo(rutaEnvio).Length;
                        Reportar(
                            5,
                            "Recuperación",
                            $"Se encontró la copia segura de {nombreArchivo}; verificando que no cambió...");
                        var hashRecuperado = await CalcularSha256Async(
                            rutaEnvio,
                            porcentaje => Reportar(
                                5 + porcentaje * 0.25,
                                "Recuperación",
                                $"Verificando copia recuperable de {nombreArchivo}: {porcentaje:N0}%..."),
                            ct);
                        if (string.Equals(hashRecuperado, entradaDiario.Sha256, StringComparison.OrdinalIgnoreCase) &&
                            totalLength == entradaDiario.PreparedLength)
                        {
                            sha256Hex = hashRecuperado;
                            fileUuid = entradaDiario.FileUuid!;
                            fueReparado = entradaDiario.WasRepaired;
                            reutilizoCopia = true;
                            Reportar(
                                32,
                                "Recuperación",
                                $"Copia segura íntegra; recuperando el último byte confirmado de {nombreArchivo}...");
                        }
                    }

                    if (!reutilizoCopia)
                    {
                        if (!File.Exists(ruta))
                            throw new FileNotFoundException(
                                $"La copia recuperable de {nombreArchivo} no superó la verificación y el original no está disponible.",
                                ruta);

                        var progresoInspeccion = new DelegatingProgress<FileSafetyProgress>(p =>
                            Reportar(p.Percentage * 0.25, TraducirEtapa(p.Stage), p.UserMessage));

                        if (policy.RequiresVerification)
                        {
                            archivoPreparado = await _fileSafety.PrepareForUploadAsync(
                                ruta, stagingArchivos, progresoInspeccion, ct);
                        }
                        else
                        {
                            Reportar(4, "Política", $"El servidor permite {extension} sin verificación profunda; creando copia inmutable para el envío...");
                            archivoPreparado = await _fileSafety.PrepareSnapshotForUploadAsync(
                                ruta, stagingArchivos, progresoInspeccion, ct);
                        }

                        fueReparado = archivoPreparado.WasRepaired;
                        var rutaTemporal = archivoPreparado.UploadPath;
                        totalLength = new FileInfo(rutaTemporal).Length;
                        if (totalLength <= 0)
                            throw new FileValidationException(
                                FileInspectionOutcome.Invalid,
                                $"{nombreArchivo} está vacío y no puede enviarse.");
                        ValidarLimitePolitica(policy, totalLength, nombreArchivo);

                        Reportar(25, "Integridad", $"Calculando huella SHA-256 de {nombreArchivo}: 0%...");
                        sha256Hex = await CalcularSha256Async(
                            rutaTemporal,
                            porcentaje => Reportar(
                                25 + porcentaje * 0.10,
                                "Integridad",
                                $"Calculando huella SHA-256 de {nombreArchivo}: {porcentaje:N0}%..."),
                            ct);

                        fileUuid = CrearUuidDeterminista(
                            rfc, deviceId, contexto.Periodo, idEnte, idAuditoria, idActo,
                            idOficio, idRequerimiento, idResultado, nombreArchivo, sha256Hex);
                        Reportar(34, "Punto de recuperación", $"Conservando una copia segura de {nombreArchivo} para resistir reinicios...");
                        rutaEnvio = await _diario.ConservarCopiaPreparadaAsync(
                            fileUuid, nombreArchivo, rutaTemporal, ct);
                        await _diario.MarcarPreparadoAsync(
                            contexto, ruta, rutaEnvio, fileUuid, sha256Hex, totalLength,
                            fueReparado, policy.RequiresVerification, policy.Version, ct);
                    }

                    if (totalLength <= 0)
                        throw new FileValidationException(
                            FileInspectionOutcome.Invalid,
                            $"{nombreArchivo} está vacío y no puede enviarse.");
                    ValidarLimitePolitica(policy, totalLength, nombreArchivo);

                    if (policy.RequiresVerification)
                    {
                        archivosVerificados++;
                        if (fueReparado)
                            archivosReparados++;
                        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            pdfVerificados++;
                            if (fueReparado)
                                pdfReparados++;
                        }
                    }
                    else
                    {
                        verificacionesOmitidas++;
                    }

                    var correlationId = Guid.NewGuid().ToString("D");

                    UploadAck? ackFinal = null;
                    var archivoFueReanudado = false;
                    Exception? ultimoError = null;
                    for (var intento = 1; intento <= MaximoReintentos; intento++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            Reportar(
                                35,
                                intento == 1 ? "Conexión segura" : "Reanudación",
                                intento == 1
                                    ? $"Consultando el estado de {nombreArchivo} en el servidor..."
                                    : $"Reintento {intento}/{MaximoReintentos}: consultando bytes conservados...");

                            var status = await clienteGrpc.GetUploadStatusAsync(
                                CrearSolicitudEstado(
                                    fileUuid, nombreArchivo, totalLength, sha256Hex, deviceId,
                                    contexto.Periodo, idEnte, idAuditoria, idActo, idOficio,
                                    idRequerimiento, idResultado, correlationId),
                                headers,
                                cancellationToken: ct);

                            if (status.AlreadyCompleted)
                            {
                                ackFinal = new UploadAck
                                {
                                    Success = true,
                                    IntegrityOk = true,
                                    Message = "Carga completada previamente; respuesta idempotente.",
                                    ReceiptId = status.ReceiptId,
                                    TraceId = status.TraceId,
                                    PolicyVersion = status.PolicyVersion,
                                    VerificationRequired = status.VerificationRequired,
                                    VerificationPerformed = status.VerificationRequired,
                                    ResumedBytes = status.NextOffset
                                };
                                archivoFueReanudado = status.NextOffset > 0;
                                break;
                            }

                            if (!status.ResumeAllowed)
                            {
                                if (string.Equals(status.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                                    intento < MaximoReintentos)
                                {
                                    Reportar(36, "Reanudación", "El servidor aún está cerrando la conexión anterior; esperando para reintentar...");
                                    await EsperarReintentoAsync(intento, ct);
                                    continue;
                                }

                                throw new InvalidOperationException(
                                    string.IsNullOrWhiteSpace(status.Message)
                                        ? "El servidor no autorizó reanudar la carga."
                                        : status.Message);
                            }

                            var offsetInicial = status.NextOffset;
                            if (offsetInicial < 0 || offsetInicial > totalLength)
                                throw new InvalidOperationException("El servidor devolvió un punto de reanudación inconsistente.");
                            await _diario.MarcarProgresoAsync(
                                contexto, ruta, offsetInicial,
                                offsetInicial > 0 ? "Resuming" : "Uploading",
                                offsetInicial > 0
                                    ? $"Servidor confirmó {offsetInicial:N0} bytes; reanudando la carga."
                                    : "Servidor confirmó una carga nueva desde el byte cero.",
                                ct);
                            offsetConfirmadoDiario = offsetInicial;
                            archivoFueReanudado |= offsetInicial > 0;
                            if (offsetInicial > 0)
                            {
                                var porcentajeConservado = (double)offsetInicial / totalLength * 100d;
                                Reportar(
                                    40 + porcentajeConservado * 0.50,
                                    "Reanudación",
                                    $"Servidor confirmó {porcentajeConservado:N0}% de {nombreArchivo}; continuando desde el byte {offsetInicial:N0}...");
                            }

                            ackFinal = await TransmitirDesdeOffsetAsync(
                                clienteGrpc, headers, rutaEnvio, fileUuid, nombreArchivo,
                                sha256Hex, totalLength, offsetInicial, correlationId,
                                jwtToken, rfc, deviceId, contexto.Periodo,
                                idEnte, idAuditoria, idActo, idOficio,
                                idRequerimiento, idResultado, Reportar, ct);
                            break;
                        }
                        catch (RpcException exception) when (
                            EsErrorTransitorio(exception.StatusCode) &&
                            intento < MaximoReintentos &&
                            !ct.IsCancellationRequested)
                        {
                            ultimoError = exception;
                            Reportar(
                                38,
                                "Reanudación",
                                $"Conexión interrumpida; el servidor conservará los bytes confirmados. Reintentando {intento + 1}/{MaximoReintentos}...");
                            await EsperarReintentoAsync(intento, ct);
                        }
                        catch (HttpRequestException exception) when (
                            intento < MaximoReintentos && !ct.IsCancellationRequested)
                        {
                            ultimoError = exception;
                            Reportar(
                                38,
                                "Reanudación",
                                $"No fue posible contactar al servidor; reintentando {intento + 1}/{MaximoReintentos} sin perder el avance confirmado...");
                            await EsperarReintentoAsync(intento, ct);
                        }
                    }

                    if (ackFinal is null)
                        throw new InvalidOperationException(
                            $"No fue posible completar {nombreArchivo} después de {MaximoReintentos} intentos.",
                            ultimoError);
                    if (!ackFinal.Success || !ackFinal.IntegrityOk)
                    {
                        throw new InvalidOperationException(
                            $"Error al subir {nombreArchivo}: {ackFinal.Message} " +
                            $"(success={ackFinal.Success}, integrityOk={ackFinal.IntegrityOk})");
                    }

                    archivosEnviados++;
                    if (archivoFueReanudado || ackFinal.ResumedBytes > 0)
                        archivosReanudados++;
                    if (!string.IsNullOrWhiteSpace(ackFinal.ReceiptId))
                        recibos.Add(ackFinal.ReceiptId);

                    await _diario.CompletarAsync(
                        contexto, ruta, totalLength, ackFinal.ReceiptId, ackFinal.TraceId, ct);

                    var mensajeFinal = fueReparado
                        ? $"{nombreArchivo} fue reparado, revalidado y enviado. Recibo digital: {MostrarId(ackFinal.ReceiptId)}."
                        : policy.RequiresVerification
                            ? $"{nombreArchivo} enviado y verificado. Recibo digital: {MostrarId(ackFinal.ReceiptId)}."
                            : $"{nombreArchivo} enviado; la verificación profunda fue omitida por política v{ackFinal.PolicyVersion}. Recibo: {MostrarId(ackFinal.ReceiptId)}.";
                    Reportar(100, "Completado", mensajeFinal, ackFinal.ReceiptId, ackFinal.TraceId);
                }
                catch (FileValidationException exception)
                {
                    Reportar(25, "Rechazado", exception.Message);
                    await _diario.MarcarProgresoAsync(
                        contexto, ruta, offsetConfirmadoDiario,
                        "Failed", exception.Message, CancellationToken.None);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    await _diario.MarcarProgresoAsync(
                        contexto, ruta, offsetConfirmadoDiario,
                        "Paused", "Carga pausada; el avance confirmado se recuperará al continuar.",
                        CancellationToken.None);
                    throw;
                }
                catch (Exception exception)
                {
                    await _diario.MarcarProgresoAsync(
                        contexto, ruta, offsetConfirmadoDiario,
                        "Paused", exception.Message, CancellationToken.None);
                    throw;
                }
                finally
                {
                    if (archivoPreparado is not null)
                        await archivoPreparado.DisposeAsync();
                }
            }

            return new ResultadoCargaArchivos(
                archivosEnviados,
                archivosVerificados,
                archivosReparados,
                pdfVerificados,
                pdfReparados,
                verificacionesOmitidas,
                archivosReanudados,
                recibos);
        }

        private static async Task<UploadAck> TransmitirDesdeOffsetAsync(
            Upload.UploadClient clienteGrpc,
            Metadata headers,
            string rutaEnvio,
            string fileUuid,
            string nombreArchivo,
            string sha256Hex,
            long totalLength,
            long offsetInicial,
            string correlationId,
            string jwtToken,
            string rfc,
            string deviceId,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idRequerimiento,
            int idResultado,
            Action<double, string, string, string?, string?> reportar,
            CancellationToken ct)
        {
            using var call = clienteGrpc.UploadStream(headers, cancellationToken: ct);
            await using var fs = new FileStream(
                rutaEnvio,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: TamanoBloque,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Seek(offsetInicial, SeekOrigin.Begin);
            var offset = offsetInicial;
            var buffer = new byte[TamanoBloque];

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var leidos = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (leidos <= 0)
                    break;

                var chunk = CrearChunk(
                    fileUuid, nombreArchivo, offset, ByteString.CopyFrom(buffer, 0, leidos),
                    sha256Hex, totalLength, correlationId, jwtToken, rfc, deviceId,
                    periodo, idEnte, idAuditoria, idActo, idOficio,
                    idRequerimiento, idResultado);
                await call.RequestStream.WriteAsync(chunk);
                offset += leidos;
                var porcentajeBytes = (double)offset / totalLength * 100d;
                reportar(
                    40 + porcentajeBytes * 0.50,
                    offsetInicial > 0 ? "Reanudación" : "Transferencia",
                    $"Enviando {nombreArchivo}: {porcentajeBytes:N0}% de sus bytes...",
                    null,
                    null);
            }

            await call.RequestStream.WriteAsync(CrearChunk(
                fileUuid, nombreArchivo, offset, ByteString.Empty,
                sha256Hex, totalLength, correlationId, jwtToken, rfc, deviceId,
                periodo, idEnte, idAuditoria, idActo, idOficio,
                idRequerimiento, idResultado));
            await call.RequestStream.CompleteAsync();
            reportar(
                95,
                "Validación del servidor",
                $"El servidor verifica integridad, política, estructura y emite el recibo de {nombreArchivo}...",
                null,
                null);
            return await call.ResponseAsync;
        }

        private static UploadChunk CrearChunk(
            string fileUuid,
            string nombreArchivo,
            long offset,
            ByteString data,
            string sha256Hex,
            long totalLength,
            string correlationId,
            string jwtToken,
            string rfc,
            string deviceId,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idRequerimiento,
            int idResultado) => new()
        {
            FileUuid = fileUuid,
            OriginalFileName = nombreArchivo,
            OriginalSourcePath = string.Empty,
            Offset = offset,
            Data = data,
            Sha256Hex = sha256Hex,
            TotalLength = totalLength,
            CorrelationId = correlationId,
            JwtToken = jwtToken,
            Rfc = rfc,
            DeviceId = deviceId,
            Periodo = periodo,
            IdEnte = idEnte,
            IdAuditoria = idAuditoria,
            IdActo = idActo,
            IdOficio = idOficio,
            ResultadoId = idResultado > 0 ? idResultado : 0,
            IdRequerimiento = idResultado > 0 ? 0 : idRequerimiento
        };

        private static UploadStatusRequest CrearSolicitudEstado(
            string fileUuid,
            string nombreArchivo,
            long totalLength,
            string sha256Hex,
            string deviceId,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idRequerimiento,
            int idResultado,
            string correlationId) => new()
        {
            FileUuid = fileUuid,
            OriginalFileName = nombreArchivo,
            TotalLength = totalLength,
            Sha256Hex = sha256Hex,
            DeviceId = deviceId,
            Periodo = periodo,
            IdEnte = idEnte,
            IdAuditoria = idAuditoria,
            IdActo = idActo,
            IdOficio = idOficio,
            ResultadoId = idResultado > 0 ? idResultado : 0,
            IdRequerimiento = idResultado > 0 ? 0 : idRequerimiento,
            CorrelationId = correlationId
        };

        private static async Task<string> CalcularSha256Async(
            string ruta,
            Action<double> reportar,
            CancellationToken ct)
        {
            await using var stream = new FileStream(
                ruta, FileMode.Open, FileAccess.Read, FileShare.Read,
                TamanoBloque, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[TamanoBloque];
            long procesados = 0;
            while (true)
            {
                var leidos = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (leidos <= 0)
                    break;
                hash.AppendData(buffer, 0, leidos);
                procesados += leidos;
                reportar(stream.Length > 0 ? (double)procesados / stream.Length * 100d : 100d);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string CrearUuidDeterminista(
            string rfc,
            string deviceId,
            int periodo,
            int idEnte,
            int idAuditoria,
            int idActo,
            int idOficio,
            int idRequerimiento,
            int idResultado,
            string nombreArchivo,
            string sha256Hex)
        {
            var canonical = string.Join("|",
                rfc.Trim().ToUpperInvariant(),
                deviceId.Trim(),
                periodo, idEnte, idAuditoria, idActo, idOficio,
                idRequerimiento, idResultado,
                nombreArchivo.Trim().ToUpperInvariant(),
                sha256Hex);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return new Guid(digest.AsSpan(0, 16)).ToString("N");
        }

        private static async Task EsperarReintentoAsync(int intento, CancellationToken ct)
        {
            var baseDelayMs = (int)Math.Pow(2, Math.Max(0, intento - 1)) * 1000;
            var jitterMs = Random.Shared.Next(100, 450);
            await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs + jitterMs), ct);
        }

        private static bool EsErrorTransitorio(StatusCode statusCode) => statusCode is
            StatusCode.Unavailable or
            StatusCode.DeadlineExceeded or
            StatusCode.ResourceExhausted or
            StatusCode.Internal or
            StatusCode.Aborted;

        private static void ValidarLimitePolitica(
            PoliticaArchivoEfectiva policy,
            long length,
            string nombreArchivo)
        {
            if (policy.MaxFileBytes.HasValue && length > policy.MaxFileBytes.Value)
            {
                throw new FileValidationException(
                    FileInspectionOutcome.TooLarge,
                    $"{nombreArchivo} pesa {length / 1024d / 1024d:N2} MB y la política v{policy.Version} " +
                    $"permite hasta {policy.MaxFileBytes.Value / 1024d / 1024d:N2} MB para {policy.Extension}.");
            }
        }

        private static string NormalizarExtension(string? extension)
        {
            var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return "(sin extensión)";
            return value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value;
        }

        private static string MostrarId(string? id) =>
            string.IsNullOrWhiteSpace(id) ? "no disponible" : id;

        private static string TraducirEtapa(FileSafetyStage stage) => stage switch
        {
            FileSafetyStage.DetectingType => "Identificación",
            FileSafetyStage.CreatingSafeCopy => "Copia segura",
            FileSafetyStage.CheckingProtection => "Contraseña y cifrado",
            FileSafetyStage.CheckingStructure => "Estructura",
            FileSafetyStage.CheckingContent => "Contenido",
            FileSafetyStage.Repairing => "Reparación",
            FileSafetyStage.Revalidating => "Revalidación",
            FileSafetyStage.Ready => "Validado",
            FileSafetyStage.Rejected => "Rechazado",
            _ => "Validación"
        };

        private sealed class DelegatingProgress<T> : IProgress<T>
        {
            private readonly Action<T> _callback;
            public DelegatingProgress(Action<T> callback) =>
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            public void Report(T value) => _callback(value);
        }

        public Task<IReadOnlyList<string>> ObtenerDocumentosExistentesAsync(
            ContextoRequerimiento contexto,
            CancellationToken ct = default)
        {
            IReadOnlyList<string> vacio = Array.Empty<string>();
            return Task.FromResult(vacio);
        }
    }
}
