using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FileXfer.Pdf;

namespace FileXfer.FileSafety;

public sealed class FileSafetyOptions
{
    public long MaxFileBytes { get; init; } = ReadPositiveLong(
        "FILEXFER_FILE_MAX_BYTES",
        1024L * 1024L * 1024L);

    public int MaxPackageEntries { get; init; } = (int)Math.Clamp(
        ReadPositiveLong("FILEXFER_PACKAGE_MAX_ENTRIES", 10_000),
        100,
        100_000);

    public long MaxPackageExpandedBytes { get; init; } = ReadPositiveLong(
        "FILEXFER_PACKAGE_MAX_EXPANDED_BYTES",
        2L * 1024L * 1024L * 1024L);

    public long MaxStructuredTextBytes { get; init; } = ReadPositiveLong(
        "FILEXFER_STRUCTURED_TEXT_MAX_BYTES",
        128L * 1024L * 1024L);

    private static long ReadPositiveLong(string name, long fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}

/// <summary>
/// Motor común de prevalidación. El cliente inspecciona una copia temporal y el servidor
/// vuelve a inspeccionar exactamente los bytes persistidos. Los formatos no verificables se
/// rechazan de forma explícita: aceptar por desconocimiento no es una validación.
/// </summary>
public sealed class DefaultFileSafetyService : IFileSafetyService
{
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".xltx", ".odt", ".ods", ".odp", ".ots",
        ".epub", ".xps", ".oxps", ".vsdx"
    };

    private static readonly HashSet<string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docm", ".dotm", ".xlsm", ".xltm", ".pptm", ".potm", ".ppsm"
    };

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xsd"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".tab", ".psv", ".md", ".ini", ".cfg", ".conf",
        ".properties", ".sql", ".yaml", ".yml", ".toml", ".ics", ".vcf", ".asc", ".csr"
    };

    private static readonly HashSet<string> LegacyOrOpaqueExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".xlt", ".ppt", ".rtf", ".wps", ".numbers", ".ai", ".eps",
        ".cdr", ".dxf", ".dwg", ".vsd", ".sqlite", ".db", ".sqlite3", ".mdb", ".bak",
        ".accdb", ".gpg", ".eml", ".msg", ".mbox", ".pst", ".ost", ".sua"
    };

    private static readonly byte[] OleHeader =
        { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] PngHeader =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly IPdfSafetyService _pdfSafety;
    private readonly FileSafetyOptions _options;

    public DefaultFileSafetyService()
        : this(new QpdfPdfSafetyService(), new FileSafetyOptions())
    {
    }

    public DefaultFileSafetyService(IPdfSafetyService pdfSafety, FileSafetyOptions? options = null)
    {
        _pdfSafety = pdfSafety ?? throw new ArgumentNullException(nameof(pdfSafety));
        _options = options ?? new FileSafetyOptions();

        if (_options.MaxFileBytes <= 0 || _options.MaxStructuredTextBytes <= 0 ||
            _options.MaxPackageExpandedBytes <= 0 || _options.MaxPackageEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Los límites de seguridad deben ser positivos.");
        }
    }

    public async Task<FileInspectionResult> InspectAsync(
        string path,
        string originalFileName,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, FileSafetyStage.DetectingType, 0, "Identificando el tipo real del archivo...");
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Rejected(FileInspectionOutcome.Invalid, "desconocido", "No se encontró el archivo que se intentó validar.", progress);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0)
            return Rejected(FileInspectionOutcome.Invalid, "desconocido", "El archivo está vacío y no se puede enviar.", progress);
        if (fileInfo.Length > _options.MaxFileBytes)
        {
            return Rejected(
                FileInspectionOutcome.TooLarge,
                "desconocido",
                $"El archivo excede el límite de inspección configurado ({FormatBytes(_options.MaxFileBytes)}).",
                progress);
        }

        var extension = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        var header = await ReadHeaderAsync(path, 16, cancellationToken);

        if (StartsWith(header, PdfHeader) && extension != ".pdf")
        {
            return Rejected(
                FileInspectionOutcome.Invalid,
                "PDF",
                "El contenido es PDF, pero la extensión del archivo no es .pdf.",
                progress);
        }

        if (extension == ".pdf")
            return await InspectPdfAsync(path, progress, cancellationToken);

        if (MacroExtensions.Contains(extension))
        {
            return Rejected(
                FileInspectionOutcome.UnsafeContent,
                "Office con macros",
                "Los documentos habilitados para macros no están permitidos. Guarde una copia sin macros y vuelva a intentarlo.",
                progress);
        }

        if (PackageExtensions.Contains(extension))
        {
            if (StartsWith(header, OleHeader))
            {
                return Rejected(
                    FileInspectionOutcome.Encrypted,
                    "paquete documental",
                    "El documento está cifrado, protegido con contraseña o no corresponde a su extensión. Guarde una copia sin protección.",
                    progress);
            }

            return InspectPackage(path, extension, progress, cancellationToken);
        }

        if (extension == ".json")
            return InspectJson(path, progress, cancellationToken);
        if (XmlExtensions.Contains(extension))
            return InspectXml(path, "XML", rejectActiveSvgContent: false, progress, cancellationToken);
        if (extension is ".svg" or ".svgz")
            return InspectSvg(path, extension, progress, cancellationToken);
        if (TextExtensions.Contains(extension))
            return InspectText(path, extension, progress, cancellationToken);
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff")
            return InspectImage(path, extension, progress, cancellationToken);
        if (extension == ".cer")
            return InspectCertificate(path, progress);

        var format = string.IsNullOrWhiteSpace(extension) ? "sin extensión" : extension;
        var reason = LegacyOrOpaqueExtensions.Contains(extension)
            ? $"El formato {extension} no permite comprobar de forma confiable integridad y ausencia de contraseña con el validador instalado. Conviértalo a PDF, Office moderno sin macros o texto abierto."
            : $"El tipo de archivo {format} no está autorizado por la política de inspección.";

        return Rejected(FileInspectionOutcome.Unsupported, format, reason, progress);
    }

    public async Task<PreparedSafeFile> PrepareForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
            throw new ArgumentException("La ruta del archivo es obligatoria.", nameof(originalPath));
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ArgumentException("La carpeta temporal es obligatoria.", nameof(stagingRoot));

        var extension = Path.GetExtension(originalPath);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pdfProgress = new DelegatingProgress<string>(message =>
                {
                    var (stage, percentage) = MapPdfProgress(message);
                    Report(progress, stage, percentage, message);
                });
                var preparedPdf = await _pdfSafety.PrepareForUploadAsync(
                    originalPath,
                    stagingRoot,
                    pdfProgress,
                    cancellationToken);
                Report(progress, FileSafetyStage.Ready, 100, "PDF listo para envío después de la validación de seguridad.");
                return new PreparedSafeFile(
                    originalPath,
                    preparedPdf.UploadPath,
                    "PDF",
                    preparedPdf.WasRepaired,
                    null,
                    null,
                    preparedPdf);
            }
            catch (PdfValidationException exception)
            {
                var outcome = MapPdfOutcome(exception.Outcome);
                Report(progress, FileSafetyStage.Rejected, 100, exception.Message);
                throw new FileValidationException(outcome, exception.Message, exception.TechnicalDetail, exception);
            }
        }

        var fullRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullRoot);
        var workingDirectory = Path.Combine(fullRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var safeFileName = "entrada" + extension;
        var snapshotPath = Path.Combine(workingDirectory, safeFileName);
        var keepDirectory = false;

        try
        {
            Report(progress, FileSafetyStage.CreatingSafeCopy, 5, "Creando una copia temporal inmutable para inspección...");
            await CopySnapshotAsync(originalPath, snapshotPath, progress, cancellationToken);

            var inspectionProgress = progress is null
                ? null
                : new DelegatingProgress<FileSafetyProgress>(item => Report(
                    progress,
                    item.Stage,
                    25 + (item.Percentage * 0.75),
                    item.UserMessage));

            var result = await InspectAsync(
                snapshotPath,
                Path.GetFileName(originalPath),
                inspectionProgress,
                cancellationToken);

            if (!result.IsAccepted)
            {
                var message = result.Outcome == FileInspectionOutcome.Damaged
                    ? result.UserMessage + " No existe una reparación automática segura para este formato; no se enviará."
                    : result.UserMessage;
                Report(progress, FileSafetyStage.Rejected, 100, message);
                throw new FileValidationException(
                    result.Outcome,
                    message,
                    result.TechnicalDetail);
            }

            keepDirectory = true;
            Report(progress, FileSafetyStage.Ready, 100, $"{result.Format} verificado y listo para envío.");
            return new PreparedSafeFile(
                originalPath,
                snapshotPath,
                result.Format,
                wasRepaired: false,
                fullRoot,
                workingDirectory);
        }
        catch
        {
            if (!keepDirectory)
                DeleteWorkingDirectorySafely(fullRoot, workingDirectory);
            throw;
        }
    }

    public async Task<PreparedSafeFile> PrepareSnapshotForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
            throw new ArgumentException("La ruta del archivo es obligatoria.", nameof(originalPath));
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ArgumentException("La carpeta temporal es obligatoria.", nameof(stagingRoot));

        var fullRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullRoot);
        var workingDirectory = Path.Combine(fullRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var extension = Path.GetExtension(originalPath);
        var snapshotPath = Path.Combine(workingDirectory, "entrada" + extension);
        try
        {
            Report(
                progress,
                FileSafetyStage.CreatingSafeCopy,
                5,
                "La política omite la verificación profunda; creando una copia inmutable para preservar integridad...");
            await CopySnapshotAsync(originalPath, snapshotPath, progress, cancellationToken);
            Report(
                progress,
                FileSafetyStage.Ready,
                100,
                "Copia segura lista. La verificación profunda fue omitida explícitamente por la política vigente.");
            return new PreparedSafeFile(
                originalPath,
                snapshotPath,
                string.IsNullOrWhiteSpace(extension) ? "sin extensión" : extension.TrimStart('.').ToUpperInvariant(),
                wasRepaired: false,
                fullRoot,
                workingDirectory);
        }
        catch
        {
            DeleteWorkingDirectorySafely(fullRoot, workingDirectory);
            throw;
        }
    }

    private async Task<FileInspectionResult> InspectPdfAsync(
        string path,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, FileSafetyStage.CheckingProtection, 20, "Comprobando que el PDF no tenga contraseña ni cifrado...");
        var result = await _pdfSafety.InspectAsync(path, cancellationToken);
        var mapped = new FileInspectionResult(
            MapPdfOutcome(result.Outcome),
            "PDF",
            result.UserMessage,
            result.TechnicalDetail);

        if (mapped.IsAccepted)
            Report(progress, FileSafetyStage.Ready, 100, mapped.UserMessage);
        else
            Report(progress, FileSafetyStage.Rejected, 100, mapped.UserMessage);
        return mapped;
    }

    private FileInspectionResult InspectPackage(
        string path,
        string extension,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, FileSafetyStage.CheckingProtection, 20, "Comprobando que el paquete no esté cifrado ni protegido...");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var centralDirectory = ReadZipCentralDirectory(stream);
            if (centralDirectory.HasEncryptedEntries)
            {
                return Rejected(
                    FileInspectionOutcome.Encrypted,
                    PackageFormat(extension),
                    "El archivo contiene entradas cifradas o protegidas con contraseña y no se enviará.",
                    progress);
            }

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            Report(progress, FileSafetyStage.CheckingStructure, 45, "Validando estructura, rutas internas y límites de expansión...");

            if (archive.Entries.Count <= 0 || archive.Entries.Count > _options.MaxPackageEntries)
                return Rejected(FileInspectionOutcome.Damaged, PackageFormat(extension), "El paquete está vacío o contiene demasiados elementos.", progress);
            if (archive.Entries.Count != centralDirectory.ExpectedCrc32.Count)
                return Rejected(FileInspectionOutcome.Damaged, PackageFormat(extension), "El directorio del paquete no coincide con sus elementos internos.", progress);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            long compressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('\0') ||
                    normalized.StartsWith('/') || normalized.Split('/').Any(part => part == "..") ||
                    !names.Add(normalized))
                {
                    return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), "El paquete contiene rutas internas inseguras o duplicadas.", progress);
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                compressedBytes = checked(compressedBytes + Math.Max(0, entry.CompressedLength));
                if (expandedBytes > _options.MaxPackageExpandedBytes)
                    return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), "El archivo podría expandirse de forma excesiva y fue bloqueado.", progress);
            }

            if (expandedBytes > 100L * Math.Max(1, compressedBytes) && expandedBytes > 64L * 1024L * 1024L)
                return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), "La relación de compresión del paquete es insegura.", progress);

            var required = RequiredPackageEntries(extension);
            foreach (var requiredEntry in required)
            {
                if (!names.Contains(requiredEntry))
                    return Rejected(FileInspectionOutcome.Damaged, PackageFormat(extension), $"El paquete está incompleto: falta {requiredEntry}.", progress);
            }

            Report(progress, FileSafetyStage.CheckingContent, 70, "Inspeccionando relaciones, macros y contenido interno...");

            if (names.Any(name => name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), "El documento contiene macros y no se enviará.", progress);

            if (names.Contains("EncryptionInfo") || names.Contains("EncryptedPackage") ||
                names.Contains("META-INF/encryption.xml") ||
                PackageManifestDeclaresEncryption(archive))
            {
                return Rejected(
                    FileInspectionOutcome.Encrypted,
                    PackageFormat(extension),
                    "El documento declara contenido cifrado o protegido y no se enviará. Guarde una copia sin contraseña.",
                    progress);
            }

            var xmlEntries = archive.Entries.Where(item =>
                         item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                         item.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var entry in xmlEntries)
            {
                ValidateXmlEntry(entry, rejectExternalRelationships: entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase));
            }

            if (extension == ".epub")
            {
                var mimetype = archive.GetEntry("mimetype");
                if (mimetype is null || ReadSmallText(mimetype, 128).Trim() != "application/epub+zip")
                    return Rejected(FileInspectionOutcome.Damaged, "EPUB", "El archivo EPUB no contiene un tipo MIME válido.", progress);
            }

            var entryIndex = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntryCanBeReadCompletely(
                    entry,
                    centralDirectory.ExpectedCrc32[entryIndex],
                    cancellationToken);
                entryIndex++;
                Report(
                    progress,
                    FileSafetyStage.CheckingContent,
                    70 + (25d * entryIndex / archive.Entries.Count),
                    $"Comprobando contenido interno: {entryIndex} de {archive.Entries.Count} partes...");
            }

            var format = PackageFormat(extension);
            var result = new FileInspectionResult(
                FileInspectionOutcome.Valid,
                format,
                $"{format} verificado: estructura válida, sin cifrado, macros ni relaciones externas.");
            Report(progress, FileSafetyStage.Ready, 100, result.UserMessage);
            return result;
        }
        catch (UnsafePackageContentException exception)
        {
            return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), exception.Message, progress, exception.InnerException?.Message);
        }
        catch (InvalidDataException exception)
        {
            return Rejected(FileInspectionOutcome.Damaged, PackageFormat(extension), "El archivo está dañado o su contenedor comprimido no se puede abrir.", progress, exception.Message);
        }
        catch (XmlException exception)
        {
            return Rejected(FileInspectionOutcome.Damaged, PackageFormat(extension), "El paquete contiene XML interno dañado o inseguro.", progress, exception.Message);
        }
        catch (OverflowException exception)
        {
            return Rejected(FileInspectionOutcome.UnsafeContent, PackageFormat(extension), "El tamaño expandido del paquete no es seguro.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectJson(
        string path,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, FileSafetyStage.CheckingStructure, 35, "Analizando la estructura JSON completa...");
        try
        {
            EnsureStructuredTextSize(path);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            _ = document.RootElement.ValueKind;
            return Accepted("JSON", "JSON verificado: sintaxis completa y codificación válidas.", progress);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or IOException)
        {
            return Rejected(FileInspectionOutcome.Damaged, "JSON", "El JSON está dañado, incompleto o tiene una codificación inválida.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectXml(
        string path,
        string format,
        bool rejectActiveSvgContent,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken,
        Stream? suppliedStream = null)
    {
        Report(progress, FileSafetyStage.CheckingStructure, 35, $"Analizando la estructura {format} de forma segura...");
        try
        {
            if (suppliedStream is null)
                EnsureStructuredTextSize(path);

            using var ownedStream = suppliedStream is null ? File.OpenRead(path) : null;
            var stream = suppliedStream ?? ownedStream!;
            using var reader = XmlReader.Create(stream, CreateSecureXmlSettings());
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rejectActiveSvgContent || reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                    reader.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
                {
                    return Rejected(FileInspectionOutcome.UnsafeContent, format, "El SVG contiene contenido activo no permitido.", progress);
                }

                if (!reader.HasAttributes)
                    continue;
                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        ((reader.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                          reader.LocalName.Equals("src", StringComparison.OrdinalIgnoreCase)) &&
                         IsExternalReference(reader.Value)) ||
                        reader.Value.Contains("url(http", StringComparison.OrdinalIgnoreCase) ||
                        reader.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Rejected(FileInspectionOutcome.UnsafeContent, format, "El SVG contiene scripts o referencias externas no permitidas.", progress);
                    }
                }
                reader.MoveToElement();
            }

            return Accepted(format, $"{format} verificado: estructura válida y sin contenido activo prohibido.", progress);
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException or IOException)
        {
            return Rejected(FileInspectionOutcome.Damaged, format, $"El archivo {format} está dañado o contiene XML inseguro.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectSvg(
        string path,
        string extension,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (extension == ".svg")
            return InspectXml(path, "SVG", rejectActiveSvgContent: true, progress, cancellationToken);

        try
        {
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var bounded = new LengthLimitedReadStream(gzip, _options.MaxStructuredTextBytes);
            return InspectXml(path, "SVG comprimido", rejectActiveSvgContent: true, progress, cancellationToken, bounded);
        }
        catch (InvalidDataException exception)
        {
            return Rejected(FileInspectionOutcome.Damaged, "SVG comprimido", "El SVG comprimido está dañado y no puede abrirse.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectText(
        string path,
        string extension,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, FileSafetyStage.CheckingStructure, 35, "Comprobando codificación y contenido de texto...");
        try
        {
            EnsureStructuredTextSize(path);
            var encoding = DetectTextEncoding(path);
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
            var buffer = new char[16 * 1024];
            long characters = 0;
            var sample = new StringBuilder(capacity: 16 * 1024);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                characters += read;
                if (sample.Length < 16 * 1024)
                    sample.Append(buffer, 0, Math.Min(read, 16 * 1024 - sample.Length));
                if (characters > _options.MaxStructuredTextBytes)
                    throw new InvalidDataException("El texto expandido excede el límite permitido.");

                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == '\0' || (char.IsControl(value) && value is not '\r' and not '\n' and not '\t' and not '\f'))
                        throw new InvalidDataException("El archivo contiene caracteres binarios o controles no permitidos.");
                }
            }

            var textSample = sample.ToString();
            if (textSample.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                textSample.Contains("BEGIN RSA PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                textSample.Contains("BEGIN EC PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                textSample.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                return Rejected(FileInspectionOutcome.UnsafeContent, "texto", "El archivo contiene una llave privada y fue bloqueado para evitar su exposición.", progress);
            }

            if (extension == ".asc")
            {
                if (textSample.Contains("BEGIN PGP MESSAGE", StringComparison.OrdinalIgnoreCase) ||
                    textSample.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                {
                    return Rejected(FileInspectionOutcome.Encrypted, "texto ASCII", "El archivo contiene datos cifrados o una llave privada y no se enviará.", progress);
                }
            }

            return Accepted("texto", "Archivo de texto verificado: codificación válida y sin contenido binario oculto.", progress);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or InvalidDataException or IOException)
        {
            return Rejected(FileInspectionOutcome.Damaged, "texto", "El archivo de texto está dañado, usa una codificación inválida o contiene datos binarios.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectImage(
        string path,
        string extension,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, FileSafetyStage.CheckingStructure, 35, "Leyendo la imagen completa y validando su estructura...");
        try
        {
            using var stream = File.OpenRead(path);
            (int Width, int Height) dimensions = extension switch
            {
                ".png" => ValidatePng(stream, cancellationToken),
                ".jpg" or ".jpeg" => ValidateJpeg(stream, cancellationToken),
                ".gif" => ValidateGif(stream, cancellationToken),
                ".bmp" => ValidateBmp(stream),
                ".tif" or ".tiff" => ValidateTiff(stream),
                _ => throw new InvalidDataException("Formato de imagen desconocido.")
            };

            ValidateDimensions(dimensions.Width, dimensions.Height);
            var format = extension.TrimStart('.').ToUpperInvariant();
            return Accepted(format, $"Imagen {format} verificada ({dimensions.Width} × {dimensions.Height} píxeles).", progress);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException or OverflowException)
        {
            return Rejected(FileInspectionOutcome.Damaged, "imagen", "La imagen está dañada, truncada o no corresponde a su extensión.", progress, exception.Message);
        }
    }

    private FileInspectionResult InspectCertificate(
        string path,
        IProgress<FileSafetyProgress>? progress)
    {
        Report(progress, FileSafetyStage.CheckingStructure, 45, "Comprobando la estructura del certificado digital...");
        try
        {
            using var certificate = new X509Certificate2(path);
            if (certificate.RawData.Length <= 0)
                throw new InvalidDataException("Certificado vacío.");
            if (certificate.HasPrivateKey)
                return Rejected(FileInspectionOutcome.UnsafeContent, "certificado X.509", "El archivo contiene una llave privada y no se enviará.", progress);
            return Accepted("certificado X.509", $"Certificado X.509 válido: {certificate.Subject}.", progress);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or InvalidDataException)
        {
            return Rejected(FileInspectionOutcome.Damaged, "certificado X.509", "El certificado está dañado o no tiene un formato X.509 válido.", progress, exception.Message);
        }
    }

    private static XmlReaderSettings CreateSecureXmlSettings() => new()
    {
        Async = false,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 128L * 1024L * 1024L,
        MaxCharactersFromEntities = 0,
        IgnoreComments = false,
        IgnoreWhitespace = false
    };

    private static void ValidateXmlEntry(ZipArchiveEntry entry, bool rejectExternalRelationships)
    {
        if (entry.Length > 128L * 1024L * 1024L)
            throw new InvalidDataException($"La parte {entry.FullName} excede el límite XML.");

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, CreateSecureXmlSettings());
        while (reader.Read())
        {
            if (!rejectExternalRelationships || reader.NodeType != XmlNodeType.Element || !reader.HasAttributes)
                continue;

            var targetMode = reader.GetAttribute("TargetMode");
            if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
                throw new UnsafePackageContentException("El documento contiene relaciones externas no permitidas y no se enviará.");
        }
    }

    private static bool PackageManifestDeclaresEncryption(ZipArchive archive)
    {
        var manifest = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("META-INF/manifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
            return false;

        using var stream = manifest.Open();
        using var reader = XmlReader.Create(stream, CreateSecureXmlSettings());
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName.Equals("encryption-data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateEntryCanBeReadCompletely(
        ZipArchiveEntry entry,
        uint expectedCrc32,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entry.Name))
        {
            if (entry.Length != 0 || expectedCrc32 != 0)
                throw new InvalidDataException($"La carpeta interna {entry.FullName} tiene datos inesperados.");
            return;
        }

        using var stream = entry.Open();
        var buffer = new byte[64 * 1024];
        long bytesRead = 0;
        var crc32 = Crc32Start();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;
            bytesRead = checked(bytesRead + read);
            crc32 = Crc32Update(crc32, buffer.AsSpan(0, read));
            if (bytesRead > entry.Length)
                throw new InvalidDataException($"La parte {entry.FullName} excede el tamaño declarado.");
        }
        if (bytesRead != entry.Length)
            throw new InvalidDataException($"La parte {entry.FullName} está truncada.");
        if (Crc32Finish(crc32) != expectedCrc32)
            throw new InvalidDataException($"La parte {entry.FullName} no coincide con su suma de comprobación CRC-32.");
    }

    private static string ReadSmallText(ZipArchiveEntry entry, int maxChars)
    {
        if (entry.Length > maxChars * 4L)
            throw new InvalidDataException("Entrada de texto fuera de límite.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 1024, false);
        var buffer = new char[maxChars + 1];
        var read = reader.Read(buffer, 0, buffer.Length);
        if (read > maxChars)
            throw new InvalidDataException("Entrada de texto fuera de límite.");
        return new string(buffer, 0, read);
    }

    private static IReadOnlyList<string> RequiredPackageEntries(string extension) => extension switch
    {
        ".docx" => new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" },
        ".xlsx" or ".xltx" => new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml" },
        ".pptx" => new[] { "[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml" },
        ".odt" or ".ods" or ".odp" or ".ots" => new[] { "mimetype", "META-INF/manifest.xml", "content.xml" },
        ".epub" => new[] { "mimetype", "META-INF/container.xml" },
        ".xps" or ".oxps" => new[] { "[Content_Types].xml", "_rels/.rels" },
        ".vsdx" => new[] { "[Content_Types].xml", "_rels/.rels", "visio/document.xml" },
        _ => Array.Empty<string>()
    };

    private static string PackageFormat(string extension) => extension switch
    {
        ".docx" => "Word",
        ".xlsx" or ".xltx" => "Excel",
        ".pptx" => "PowerPoint",
        ".odt" or ".ods" or ".odp" or ".ots" => "OpenDocument",
        ".epub" => "EPUB",
        ".xps" or ".oxps" => "XPS",
        ".vsdx" => "Visio",
        _ => "paquete documental"
    };

    private static ZipCentralDirectoryInfo ReadZipCentralDirectory(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 22)
            throw new InvalidDataException("Directorio ZIP ausente.");

        var searchLength = (int)Math.Min(stream.Length, 65_557);
        var tail = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        ReadExactly(stream, tail);

        var eocd = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054b50)
            {
                eocd = index;
                break;
            }
        }
        if (eocd < 0)
            throw new InvalidDataException("No se encontró el directorio central ZIP.");

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
        if (entryCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
            throw new InvalidDataException("Los paquetes ZIP64 no están autorizados para esta transferencia.");
        if ((long)directoryOffset + directorySize > stream.Length)
            throw new InvalidDataException("El directorio central ZIP está fuera del archivo.");

        stream.Position = directoryOffset;
        var fixedHeader = new byte[46];
        var expectedCrc32 = new List<uint>(entryCount);
        var hasEncryptedEntries = false;
        for (var index = 0; index < entryCount; index++)
        {
            ReadExactly(stream, fixedHeader);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader) != 0x02014b50)
                throw new InvalidDataException("Entrada inválida en el directorio ZIP.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(8, 2));
            if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0)
                hasEncryptedEntries = true;
            expectedCrc32.Add(BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(16, 4)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(32, 2));
            stream.Position = checked(stream.Position + nameLength + extraLength + commentLength);
        }
        return new ZipCentralDirectoryInfo(hasEncryptedEntries, expectedCrc32);
    }

    private static (int Width, int Height) ValidatePng(Stream stream, CancellationToken cancellationToken)
    {
        var signature = new byte[8];
        ReadExactly(stream, signature);
        if (!signature.SequenceEqual(PngHeader))
            throw new InvalidDataException("Firma PNG inválida.");

        var foundHeader = false;
        var foundEnd = false;
        var width = 0;
        var height = 0;
        var header = new byte[8];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(stream, header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            if (length > 256 * 1024 * 1024)
                throw new InvalidDataException("Chunk PNG fuera de límite.");
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var crc = Crc32Start();
            crc = Crc32Update(crc, header.AsSpan(4, 4));
            var remaining = (long)length;
            var buffer = new byte[64 * 1024];
            var firstData = new byte[Math.Min(13, (int)Math.Min(length, 13))];
            var captured = 0;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    throw new EndOfStreamException();
                if (captured < firstData.Length)
                {
                    var copy = Math.Min(read, firstData.Length - captured);
                    Array.Copy(buffer, 0, firstData, captured, copy);
                    captured += copy;
                }
                crc = Crc32Update(crc, buffer.AsSpan(0, read));
                remaining -= read;
            }

            var expectedBytes = new byte[4];
            ReadExactly(stream, expectedBytes);
            var expected = BinaryPrimitives.ReadUInt32BigEndian(expectedBytes);
            if (Crc32Finish(crc) != expected)
                throw new InvalidDataException($"CRC inválido en chunk PNG {type}.");

            if (type == "IHDR")
            {
                if (foundHeader || length != 13)
                    throw new InvalidDataException("IHDR PNG inválido.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(firstData.AsSpan(0, 4)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(firstData.AsSpan(4, 4)));
                foundHeader = true;
            }
            if (type == "IEND")
            {
                if (length != 0)
                    throw new InvalidDataException("IEND PNG inválido.");
                foundEnd = true;
                break;
            }
        }

        if (!foundHeader || !foundEnd || stream.Position != stream.Length)
            throw new InvalidDataException("PNG incompleto o con datos posteriores al cierre.");
        return (width, height);
    }

    private static (int Width, int Height) ValidateJpeg(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            throw new InvalidDataException("Firma JPEG inválida.");
        var width = 0;
        var height = 0;
        var inScan = false;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = stream.ReadByte();
            if (value < 0)
                break;
            if (value != 0xFF)
            {
                if (inScan)
                    continue;
                throw new InvalidDataException("Marcador JPEG inválido.");
            }

            int marker;
            do marker = stream.ReadByte(); while (marker == 0xFF);
            if (marker < 0)
                throw new EndOfStreamException();
            if (inScan && marker == 0x00)
                continue;
            if (marker == 0xD9)
            {
                if (width <= 0 || height <= 0 || stream.Position != stream.Length)
                    throw new InvalidDataException("JPEG incompleto o con datos posteriores al cierre.");
                return (width, height);
            }
            if (marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
                continue;

            var lengthBytes = new byte[2];
            ReadExactly(stream, lengthBytes);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || stream.Position + length - 2 > stream.Length)
                throw new InvalidDataException("Segmento JPEG truncado.");

            if (IsStartOfFrame(marker))
            {
                var frame = new byte[5];
                ReadExactly(stream, frame);
                height = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3, 2));
                stream.Position += length - 2 - frame.Length;
            }
            else
            {
                stream.Position += length - 2;
            }
            inScan = marker == 0xDA;
        }
        throw new InvalidDataException("No se encontró el cierre JPEG.");
    }

    private static bool IsStartOfFrame(int marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static (int Width, int Height) ValidateGif(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[13];
        ReadExactly(stream, header);
        var signature = Encoding.ASCII.GetString(header, 0, 6);
        if (signature is not "GIF87a" and not "GIF89a")
            throw new InvalidDataException("Firma GIF inválida.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        if ((header[10] & 0x80) != 0)
            SkipExactly(stream, 3L * (1 << ((header[10] & 0x07) + 1)));

        var foundImage = false;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = stream.ReadByte();
            if (marker == 0x3B)
            {
                if (!foundImage || stream.Position != stream.Length)
                    throw new InvalidDataException("GIF vacío o con datos posteriores al cierre.");
                return (width, height);
            }
            if (marker == 0x21)
            {
                if (stream.ReadByte() < 0)
                    throw new EndOfStreamException();
                SkipGifSubBlocks(stream);
                continue;
            }
            if (marker != 0x2C)
                throw new InvalidDataException("Bloque GIF inválido.");

            var descriptor = new byte[9];
            ReadExactly(stream, descriptor);
            if ((descriptor[8] & 0x80) != 0)
                SkipExactly(stream, 3L * (1 << ((descriptor[8] & 0x07) + 1)));
            if (stream.ReadByte() is < 2 or > 12)
                throw new InvalidDataException("Código LZW GIF inválido.");
            SkipGifSubBlocks(stream);
            foundImage = true;
        }
        throw new InvalidDataException("No se encontró el cierre GIF.");
    }

    private static (int Width, int Height) ValidateBmp(Stream stream)
    {
        var header = new byte[26];
        ReadExactly(stream, header);
        if (header[0] != 'B' || header[1] != 'M')
            throw new InvalidDataException("Firma BMP inválida.");
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10, 4));
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4));
        if (declaredSize != stream.Length || pixelOffset >= stream.Length || dibSize < 12)
            throw new InvalidDataException("Cabecera BMP inconsistente.");

        if (dibSize == 12)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2));
            return (width, height);
        }

        if (dibSize < 40)
            throw new InvalidDataException("DIB BMP no compatible.");
        var width32 = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var height32 = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22, 4));
        return (Math.Abs(width32), Math.Abs(height32));
    }

    private static (int Width, int Height) ValidateTiff(Stream stream)
    {
        var header = new byte[8];
        ReadExactly(stream, header);
        var littleEndian = header[0] == 'I' && header[1] == 'I';
        var bigEndian = header[0] == 'M' && header[1] == 'M';
        if (!littleEndian && !bigEndian)
            throw new InvalidDataException("Firma TIFF inválida.");
        ushort ReadU16(ReadOnlySpan<byte> data) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data)
            : BinaryPrimitives.ReadUInt16BigEndian(data);
        uint ReadU32(ReadOnlySpan<byte> data) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : BinaryPrimitives.ReadUInt32BigEndian(data);
        if (ReadU16(header.AsSpan(2, 2)) != 42)
            throw new InvalidDataException("Versión TIFF inválida.");
        var ifdOffset = ReadU32(header.AsSpan(4, 4));
        if (ifdOffset < 8 || ifdOffset + 2 > stream.Length)
            throw new InvalidDataException("IFD TIFF fuera del archivo.");

        stream.Position = ifdOffset;
        var countBytes = new byte[2];
        ReadExactly(stream, countBytes);
        var count = ReadU16(countBytes);
        if (count == 0 || count > 4096 || stream.Position + count * 12L + 4 > stream.Length)
            throw new InvalidDataException("Directorio TIFF inválido.");

        var width = 0;
        var height = 0;
        var entry = new byte[12];
        for (var index = 0; index < count; index++)
        {
            ReadExactly(stream, entry);
            var tag = ReadU16(entry.AsSpan(0, 2));
            var type = ReadU16(entry.AsSpan(2, 2));
            var itemCount = ReadU32(entry.AsSpan(4, 4));
            if (itemCount != 1 || (type != 3 && type != 4))
                continue;
            var value = type == 3 ? ReadU16(entry.AsSpan(8, 2)) : checked((int)ReadU32(entry.AsSpan(8, 4)));
            if (tag == 256) width = value;
            if (tag == 257) height = value;
        }
        return (width, height);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 100_000 || height > 100_000 ||
            (long)width * height > 500_000_000L)
        {
            throw new InvalidDataException("Dimensiones de imagen inválidas o excesivas.");
        }
    }

    private static void SkipGifSubBlocks(Stream stream)
    {
        while (true)
        {
            var length = stream.ReadByte();
            if (length < 0)
                throw new EndOfStreamException();
            if (length == 0)
                return;
            SkipExactly(stream, length);
        }
    }

    private static void SkipExactly(Stream stream, long bytes)
    {
        if (bytes < 0 || stream.Position + bytes > stream.Length)
            throw new EndOfStreamException();
        stream.Position += bytes;
    }

    private static uint Crc32Start() => 0xFFFFFFFFu;

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }

    private static uint Crc32Finish(uint crc) => ~crc;

    private static Encoding DetectTextEncoding(string path)
    {
        Span<byte> headerBuffer = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var bytesRead = stream.Read(headerBuffer);
        var header = headerBuffer[..bytesRead];
        if (header.StartsWith(new byte[] { 0xFF, 0xFE }))
            return new UnicodeEncoding(false, true, true);
        if (header.StartsWith(new byte[] { 0xFE, 0xFF }))
            return new UnicodeEncoding(true, true, true);
        if (header.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return new UTF8Encoding(true, true);
        return new UTF8Encoding(false, true);
    }

    private void EnsureStructuredTextSize(string path)
    {
        if (new FileInfo(path).Length > _options.MaxStructuredTextBytes)
            throw new InvalidDataException($"El archivo excede el límite de texto estructurado ({FormatBytes(_options.MaxStructuredTextBytes)}).");
    }

    private async Task CopySnapshotAsync(
        string sourcePath,
        string destinationPath,
        IProgress<FileSafetyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists)
            throw new FileValidationException(FileInspectionOutcome.Invalid, "No se encontró el archivo que se intentó validar.");
        if (sourceInfo.Length <= 0)
            throw new FileValidationException(FileInspectionOutcome.Invalid, "El archivo está vacío y no se puede enviar.");
        if (sourceInfo.Length > _options.MaxFileBytes)
            throw new FileValidationException(FileInspectionOutcome.TooLarge, $"El archivo excede el límite permitido ({FormatBytes(_options.MaxFileBytes)}).");

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                break;
            copied = checked(copied + read);
            if (copied > _options.MaxFileBytes)
                throw new FileValidationException(FileInspectionOutcome.TooLarge, "El archivo cambió durante la copia y excedió el límite permitido.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var percentage = source.Length > 0 ? 5 + (20d * copied / source.Length) : 25;
            Report(progress, FileSafetyStage.CreatingSafeCopy, percentage, $"Creando copia segura: {Math.Min(100, copied * 100d / Math.Max(1, source.Length)):N0}%");
        }
        await destination.FlushAsync(cancellationToken);

        if (copied != sourceInfo.Length || source.Length != sourceInfo.Length)
            throw new FileValidationException(FileInspectionOutcome.Invalid, "El archivo cambió mientras se preparaba. Vuelva a seleccionarlo e intente de nuevo.");
    }

    private static async Task<byte[]> ReadHeaderAsync(string path, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, count, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
        return buffer.AsSpan(0, read).ToArray();
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static bool IsExternalReference(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is not "data";

    private static FileInspectionResult Accepted(
        string format,
        string message,
        IProgress<FileSafetyProgress>? progress)
    {
        Report(progress, FileSafetyStage.Ready, 100, message);
        return new FileInspectionResult(FileInspectionOutcome.Valid, format, message);
    }

    private static FileInspectionResult Rejected(
        FileInspectionOutcome outcome,
        string format,
        string message,
        IProgress<FileSafetyProgress>? progress,
        string? detail = null)
    {
        Report(progress, FileSafetyStage.Rejected, 100, message);
        return new FileInspectionResult(outcome, format, message, detail);
    }

    private static void Report(
        IProgress<FileSafetyProgress>? progress,
        FileSafetyStage stage,
        double percentage,
        string message) =>
        progress?.Report(new FileSafetyProgress(stage, Math.Clamp(percentage, 0, 100), message));

    private static (FileSafetyStage Stage, double Percentage) MapPdfProgress(string message)
    {
        if (message.Contains("intentando reparar", StringComparison.OrdinalIgnoreCase))
            return (FileSafetyStage.Repairing, 55);
        if (message.Contains("reparado", StringComparison.OrdinalIgnoreCase))
            return (FileSafetyStage.Revalidating, 95);
        if (message.Contains("contraseña", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cifrado", StringComparison.OrdinalIgnoreCase))
            return (FileSafetyStage.CheckingProtection, 30);
        if (message.Contains("verificado", StringComparison.OrdinalIgnoreCase))
            return (FileSafetyStage.Ready, 100);
        return (FileSafetyStage.CheckingStructure, 20);
    }

    private static FileInspectionOutcome MapPdfOutcome(PdfInspectionOutcome outcome) => outcome switch
    {
        PdfInspectionOutcome.Valid => FileInspectionOutcome.Valid,
        PdfInspectionOutcome.Encrypted => FileInspectionOutcome.Encrypted,
        PdfInspectionOutcome.Damaged => FileInspectionOutcome.Damaged,
        PdfInspectionOutcome.UnsafeActiveContent => FileInspectionOutcome.UnsafeContent,
        PdfInspectionOutcome.Invalid => FileInspectionOutcome.Invalid,
        PdfInspectionOutcome.TooLarge => FileInspectionOutcome.TooLarge,
        PdfInspectionOutcome.ToolUnavailable => FileInspectionOutcome.ToolUnavailable,
        PdfInspectionOutcome.TimedOut => FileInspectionOutcome.TimedOut,
        _ => FileInspectionOutcome.Invalid
    };

    private static string FormatBytes(long bytes)
    {
        var value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }

    private static void DeleteWorkingDirectorySafely(string rootPath, string workingDirectory)
    {
        try
        {
            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(workingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class LengthLimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _read;

        public LengthLimitedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _read = checked(_read + read);
            if (_read > _maxBytes)
                throw new InvalidDataException("El contenido expandido excede el límite permitido.");
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            _read = checked(_read + read);
            if (_read > _maxBytes)
                throw new InvalidDataException("El contenido expandido excede el límite permitido.");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelegatingProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public DelegatingProgress(Action<T> callback) =>
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

    private sealed class UnsafePackageContentException : IOException
    {
        public UnsafePackageContentException(string message)
            : base(message)
        {
        }
    }

    private sealed record ZipCentralDirectoryInfo(
        bool HasEncryptedEntries,
        IReadOnlyList<uint> ExpectedCrc32);
}
