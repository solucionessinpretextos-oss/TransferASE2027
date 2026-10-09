// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Servicios/ServicioCargaArchivos.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Servicio gRPC de carga de archivos.
// Interacción: Envía metadata, chunks y hash final hacia UploadGrpcService.
// Regla de mantenimiento: Mantener URLs, JWT y manejo de errores consistentes; no duplicar lógica en code-behind.
// Notas específicas:
// - Carga de archivos: el hash valida integridad física; el registro en Transfer_ArchivosLocales valida catalogación de negocio.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Google.Protobuf;
using TransferClienteASE.Modelos;
using FileXfer.Proto;
using FileXfer.Pdf;


// ============================================================================
//  ServicioCargaArchivos
// ============================================================================
//  Se encarga de enviar físicamente los archivos al servidor usando gRPC streaming.
//  Cada archivo se divide en bloques de 1 MB para evitar cargarlo completo en memoria.
//
//  Además de bytes, cada chunk transporta contexto de negocio:
//      IdEnte, IdAuditoria, IdActo, IdOficio y RequerimientoId o ResultadoId.
//
//  La regla de decisión es:
//      Si ContextoRequerimiento.ResultadoId > 0, el archivo se registra por resultado.
//      Si no hay ResultadoId, se registra por RequerimientoId.
//
//  Para acto 21, ResultadoId debe ser BUZ_Resultados_CedulaEvaluacion.ID.
// ============================================================================
namespace TransferClienteASE.Servicios
{
    public sealed record ProgresoCargaArchivo(
        string RutaOriginal,
        double Porcentaje,
        string Mensaje);

    public sealed record ResultadoCargaArchivos(
        int ArchivosEnviados,
        int PdfVerificados,
        int PdfReparados);

    public sealed class ServicioCargaArchivos
    {
        private readonly ServicioConfiguracionCliente _configuracion;
        private readonly IPdfSafetyService _pdfSafety;

        // Tamaño de cada bloque enviado por gRPC.
        // 1 MB es un equilibrio razonable: reduce overhead de llamadas sin exigir demasiada RAM
        // en equipos cliente antiguos.
        private const int TamanoBloque = 1024 * 1024; // 1MB

        public ServicioCargaArchivos(
            ServicioConfiguracionCliente configuracion,
            IPdfSafetyService? pdfSafety = null)
        {
            _configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
            _pdfSafety = pdfSafety ?? new QpdfPdfSafetyService();
        }

        /// <summary>
        /// Envía uno o más archivos al servidor.
        ///
        /// La función valida sesión, arma el contexto funcional, calcula SHA-256 y reporta avance
        /// por archivo. Si el servidor responde integrityOk=false, se considera error porque el
        /// archivo recibido no coincide con el hash calculado en cliente.
        /// </summary>
        public async Task<ResultadoCargaArchivos> EnviarArchivosAsync(
            ContextoRequerimiento contexto,
            IReadOnlyList<string> rutasArchivos,
            IProgress<ProgresoCargaArchivo> progreso,
            CancellationToken ct)
        {
            if (contexto == null) throw new ArgumentNullException(nameof(contexto));
            if (rutasArchivos == null) throw new ArgumentNullException(nameof(rutasArchivos));
            if (progreso == null) throw new ArgumentNullException(nameof(progreso));

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

            // ResultadoId tiene prioridad sobre RequerimientoId.
            // Esto activa el flujo de carga por resultado para acto 16 y acto 21.
            var idResultado = contexto.ResultadoId > 0 ? contexto.ResultadoId : contexto.IdResultado;

            var idRequerimiento = contexto.Requerimiento?.Id ?? 0;

            if (idEnte <= 0 || idAuditoria <= 0 || idActo <= 0 || idOficio <= 0 ||
                (idResultado > 0 ? false : (idRequerimiento <= 0)))
            {
                throw new InvalidOperationException(
                    $"ContextoRequerimiento incompleto. " +
                    $"IdEnte={idEnte}, IdAuditoria={idAuditoria}, IdActo={idActo}, IdOficio={idOficio}, " +
                    $"IdRequerimiento={idRequerimiento}, ResultadoId={idResultado}");
            }

            Console.WriteLine(
                $"[ServicioCargaArchivos] Subiendo con contexto: " +
                $"Ente={idEnte}, Aud={idAuditoria}, Acto={idActo}, Oficio={idOficio}, " +
                (idResultado > 0
                    ? $"ResultadoId={idResultado} (RequerimientoId=0)"
                    : $"Req={idRequerimiento}"));

            var handler = _configuracion.CrearManejadorHttpSeguro();

            using var channel = GrpcChannel.ForAddress(_configuracion.DireccionGrpc, new GrpcChannelOptions
            {
                HttpHandler = handler
            });

            var clienteGrpc = new Upload.UploadClient(channel);

            var archivosEnviados = 0;
            var pdfVerificados = 0;
            var pdfReparados = 0;
            var stagingPdf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASE",
                "TransferClienteASE",
                "PdfPreflight");

            foreach (var ruta in rutasArchivos)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
                    continue;

                var rutaEnvio = ruta;
                PreparedPdfFile? pdfPreparado = null;

                try
                {
                    if (Path.GetExtension(ruta).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        var progresoPdf = new Progress<string>(mensaje =>
                            progreso.Report(new ProgresoCargaArchivo(ruta, 0, mensaje)));

                        pdfPreparado = await _pdfSafety.PrepareForUploadAsync(
                            ruta,
                            stagingPdf,
                            progresoPdf,
                            ct);

                        rutaEnvio = pdfPreparado.UploadPath;
                        pdfVerificados++;
                        if (pdfPreparado.WasRepaired)
                            pdfReparados++;
                    }

                    var headers = new Metadata
                    {
                        { "Authorization", $"Bearer {jwtToken}" }
                    };

                    using var call = clienteGrpc.UploadStream(headers, cancellationToken: ct);

                    var fileUuid = Guid.NewGuid().ToString("N");
                    // El nombre se conserva; la ruta local del equipo no se transmite al servidor.
                    var nombreArchivo = Path.GetFileName(ruta);

                    long offset = 0;
                    var buffer = new byte[TamanoBloque];

                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    using var fs = new FileStream(
                        rutaEnvio,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: TamanoBloque,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                    var total = fs.Length;
                    progreso.Report(new ProgresoCargaArchivo(ruta, 0, $"Enviando {nombreArchivo}..."));

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        var leidos = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                        if (leidos <= 0)
                            break;

                        hash.AppendData(buffer, 0, leidos);

                        var chunk = new UploadChunk
                        {
                            FileUuid = fileUuid,
                            OriginalFileName = nombreArchivo,
                            OriginalSourcePath = string.Empty,
                            Offset = offset,
                            Data = ByteString.CopyFrom(buffer, 0, leidos),

                            Sha256Hex = "",

                            JwtToken = jwtToken,
                            Rfc = rfc,
                            DeviceId = deviceId,

                            IdEnte = idEnte,
                            IdAuditoria = idAuditoria,
                            IdActo = idActo,
                            IdOficio = idOficio,
                            Periodo = contexto.Periodo,

                            // Solo uno de estos dos campos debe viajar con valor:
                            // - ResultadoId para Cédula/Informe Individual.
                            // - IdRequerimiento para actos normales.
                            ResultadoId = idResultado > 0 ? idResultado : 0,
                            IdRequerimiento = idResultado > 0 ? 0 : idRequerimiento
                        };

                        await call.RequestStream.WriteAsync(chunk);

                        offset += leidos;
                        var porcentaje = total > 0 ? (double)offset / total * 100.0 : 100.0;
                        progreso.Report(new ProgresoCargaArchivo(
                            ruta,
                            porcentaje,
                            $"Enviando {nombreArchivo}: {porcentaje:N0}%"));
                    }

                    var sha256Hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

                    var finalMeta = new UploadChunk
                    {
                        FileUuid = fileUuid,
                        OriginalFileName = nombreArchivo,
                        OriginalSourcePath = string.Empty,
                        Offset = offset,
                        Data = ByteString.Empty,
                        Sha256Hex = sha256Hex,

                        JwtToken = jwtToken,
                        Rfc = rfc,
                        DeviceId = deviceId,

                        IdEnte = idEnte,
                        IdAuditoria = idAuditoria,
                        IdActo = idActo,
                        IdOficio = idOficio,
                        Periodo = contexto.Periodo,

                        ResultadoId = idResultado > 0 ? idResultado : 0,
                        IdRequerimiento = idResultado > 0 ? 0 : idRequerimiento
                    };

                    await call.RequestStream.WriteAsync(finalMeta);
                    await call.RequestStream.CompleteAsync();
                    var ack = await call.ResponseAsync;

                    if (!ack.Success || !ack.IntegrityOk)
                    {
                        throw new InvalidOperationException(
                            $"Error al subir {nombreArchivo}: {ack.Message} " +
                            $"(success={ack.Success}, integrityOk={ack.IntegrityOk})");
                    }

                    archivosEnviados++;
                    var mensajeFinal = pdfPreparado?.WasRepaired == true
                        ? $"{nombreArchivo} enviado correctamente después de reparar y verificar una copia."
                        : $"{nombreArchivo} enviado y verificado correctamente.";
                    progreso.Report(new ProgresoCargaArchivo(ruta, 100.0, mensajeFinal));
                }
                finally
                {
                    if (pdfPreparado is not null)
                        await pdfPreparado.DisposeAsync();
                }
            }

            return new ResultadoCargaArchivos(archivosEnviados, pdfVerificados, pdfReparados);
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
