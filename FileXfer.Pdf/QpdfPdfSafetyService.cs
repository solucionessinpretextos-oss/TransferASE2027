using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileXfer.Pdf;

public sealed class PdfSafetyOptions
{
    public long MaxPdfBytes { get; init; } = ReadPositiveLong("FILEXFER_PDF_MAX_BYTES", 1024L * 1024L * 1024L);

    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(
        ReadPositiveLong("FILEXFER_PDF_TIMEOUT_SECONDS", 300));

    public string? QpdfExecutablePath { get; init; } = Environment.GetEnvironmentVariable("FILEXFER_QPDF_PATH");

    public int MaxConcurrentProcesses { get; init; } = (int)Math.Clamp(
        ReadPositiveLong("FILEXFER_PDF_MAX_PROCESSES", 4),
        1,
        32);

    private static long ReadPositiveLong(string name, long fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}

public sealed class QpdfPdfSafetyService : IPdfSafetyService
{
    private const int MaxDiagnosticChars = 32 * 1024;
    private static readonly string[] DangerousPdfJsonTokens =
    {
        "\"/JavaScript\"",
        "\"/JS\"",
        "\"/OpenAction\"",
        "\"/AA\"",
        "\"/Launch\"",
        "\"/EmbeddedFiles\"",
        "\"/RichMedia\"",
        "\"/SubmitForm\"",
        "\"/ImportData\"",
        "\"/GoToR\""
    };
    private readonly PdfSafetyOptions _options;
    private readonly SemaphoreSlim _processGate;

    public QpdfPdfSafetyService()
        : this(new PdfSafetyOptions())
    {
    }

    public QpdfPdfSafetyService(PdfSafetyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxPdfBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPdfBytes debe ser mayor que cero.");
        if (_options.ProcessTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "ProcessTimeout debe ser mayor que cero.");
        if (_options.MaxConcurrentProcesses is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentProcesses debe estar entre 1 y 32.");

        _processGate = new SemaphoreSlim(_options.MaxConcurrentProcesses, _options.MaxConcurrentProcesses);
    }

    public async Task<PdfInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Invalid,
                null,
                "No se encontró el archivo PDF que se intentó validar.");
        }

        var file = new FileInfo(path);
        if (file.Length <= 0)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Invalid,
                null,
                "El archivo PDF está vacío y no se puede enviar.");
        }

        if (file.Length > _options.MaxPdfBytes)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.TooLarge,
                null,
                $"El PDF excede el límite de validación configurado ({FormatBytes(_options.MaxPdfBytes)}).");
        }

        var encryption = await RunQpdfAsync(new[] { "--is-encrypted", path }, cancellationToken);
        if (encryption.ToolUnavailable)
            return ToolUnavailable(encryption.Detail);
        if (encryption.TimedOut)
            return TimedOut(encryption.Detail);
        if (encryption.ExitCode == 0)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Encrypted,
                null,
                "El PDF está cifrado o protegido. No se enviará; guarde una copia sin contraseña y vuelva a intentarlo.",
                encryption.Detail);
        }

        // Para --is-encrypted, qpdf usa 2 para indicar que el documento no está cifrado.
        if (encryption.ExitCode != 2)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Invalid,
                null,
                "No fue posible determinar de forma segura si el PDF está protegido.",
                encryption.Detail);
        }

        var check = await RunQpdfAsync(new[] { "--check", path }, cancellationToken);
        if (check.ToolUnavailable)
            return ToolUnavailable(check.Detail);
        if (check.TimedOut)
            return TimedOut(check.Detail);
        if (check.ExitCode != 0)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Damaged,
                null,
                "El PDF tiene daños o inconsistencias estructurales.",
                check.Detail);
        }

        var pages = await RunQpdfAsync(new[] { "--show-npages", path }, cancellationToken);
        if (pages.ToolUnavailable)
            return ToolUnavailable(pages.Detail);
        if (pages.TimedOut)
            return TimedOut(pages.Detail);

        if (pages.ExitCode != 0 ||
            !int.TryParse(pages.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pageCount) ||
            pageCount <= 0)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Invalid,
                null,
                "El archivo no contiene un catálogo de páginas PDF válido.",
                pages.Detail);
        }

        // qpdf serializa diccionarios y nombres PDF incluso cuando están dentro de object
        // streams. Se omiten los datos de streams y se recorre toda la salida de forma acotada
        // para detectar acciones activas sin cargar el documento completo en memoria.
        var activeContent = await RunQpdfAsync(
            new[] { "--json", "--json-stream-data=none", path },
            cancellationToken,
            scanForActiveContent: true);
        if (activeContent.ToolUnavailable)
            return ToolUnavailable(activeContent.Detail);
        if (activeContent.TimedOut)
            return TimedOut(activeContent.Detail);
        if (activeContent.ExitCode != 0)
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.Damaged,
                null,
                "El PDF no pudo inspeccionarse completamente para detectar contenido activo.",
                activeContent.Detail);
        }
        if (!string.IsNullOrWhiteSpace(activeContent.DetectedDangerousToken))
        {
            return new PdfInspectionResult(
                PdfInspectionOutcome.UnsafeActiveContent,
                null,
                "El PDF contiene acciones o contenido activo no permitido y no se enviará.",
                $"PDF active-content token: {activeContent.DetectedDangerousToken}");
        }

        return new PdfInspectionResult(
            PdfInspectionOutcome.Valid,
            pageCount,
            $"PDF verificado correctamente ({pageCount} página{(pageCount == 1 ? string.Empty : "s")}).");
    }

    public async Task<PreparedPdfFile> PrepareForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
            throw new ArgumentException("La ruta del PDF es obligatoria.", nameof(originalPath));
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ArgumentException("La carpeta temporal es obligatoria.", nameof(stagingRoot));

        var fullRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullRoot);
        var workingDirectory = Path.Combine(fullRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var snapshotPath = Path.Combine(workingDirectory, "entrada.pdf");
        var repairedPath = Path.Combine(workingDirectory, "reparado.pdf");
        var keepDirectory = false;

        try
        {
            progress?.Report("Validando que el PDF abra correctamente y no tenga contraseña...");
            var sourceInfo = new FileInfo(originalPath);
            if (!sourceInfo.Exists)
                throw new PdfValidationException(
                    PdfInspectionOutcome.Invalid,
                    "No se encontró el archivo PDF que se intentó validar.");
            if (sourceInfo.Length > _options.MaxPdfBytes)
                throw new PdfValidationException(
                    PdfInspectionOutcome.TooLarge,
                    $"El PDF excede el límite de validación configurado ({FormatBytes(_options.MaxPdfBytes)}).");

            await CopySnapshotAsync(
                originalPath,
                snapshotPath,
                _options.MaxPdfBytes,
                cancellationToken);

            var inspection = await InspectAsync(snapshotPath, cancellationToken);
            if (inspection.IsAccepted)
            {
                progress?.Report(inspection.UserMessage);
                keepDirectory = true;
                return new PreparedPdfFile(
                    originalPath,
                    snapshotPath,
                    fullRoot,
                    workingDirectory,
                    wasRepaired: false,
                    inspection.PageCount!.Value);
            }

            if (inspection.Outcome == PdfInspectionOutcome.Encrypted)
            {
                progress?.Report(inspection.UserMessage);
                throw new PdfValidationException(inspection.Outcome, inspection.UserMessage, inspection.TechnicalDetail);
            }

            if (inspection.Outcome != PdfInspectionOutcome.Damaged)
                throw new PdfValidationException(inspection.Outcome, inspection.UserMessage, inspection.TechnicalDetail);

            progress?.Report("Se detectaron daños en el PDF; intentando reparar una copia segura. El original no será modificado...");
            var repair = await RunQpdfAsync(new[] { snapshotPath, repairedPath }, cancellationToken);
            if (repair.ToolUnavailable)
                throw new PdfValidationException(PdfInspectionOutcome.ToolUnavailable, ToolUnavailable(repair.Detail).UserMessage, repair.Detail);
            if (repair.TimedOut)
                throw new PdfValidationException(PdfInspectionOutcome.TimedOut, TimedOut(repair.Detail).UserMessage, repair.Detail);

            if ((repair.ExitCode != 0 && repair.ExitCode != 3) || !File.Exists(repairedPath))
            {
                throw new PdfValidationException(
                    PdfInspectionOutcome.Invalid,
                    "El PDF está dañado y no fue posible repararlo de forma segura. No se enviará.",
                    repair.Detail);
            }

            var repairedInspection = await InspectAsync(repairedPath, cancellationToken);
            if (!repairedInspection.IsAccepted)
            {
                throw new PdfValidationException(
                    repairedInspection.Outcome,
                    "Se intentó reparar el PDF, pero la copia resultante no superó la validación. No se enviará.",
                    repairedInspection.TechnicalDetail);
            }

            progress?.Report(
                "PDF reparado y verificado; se enviará la copia reparada. El archivo original permanece sin cambios.");
            keepDirectory = true;
            return new PreparedPdfFile(
                originalPath,
                repairedPath,
                fullRoot,
                workingDirectory,
                wasRepaired: true,
                repairedInspection.PageCount!.Value);
        }
        finally
        {
            if (!keepDirectory)
                DeleteWorkingDirectory(fullRoot, workingDirectory);
        }
    }

    private async Task<ProcessResult> RunQpdfAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        bool scanForActiveContent = false)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            return await RunQpdfProcessAsync(arguments, cancellationToken, scanForActiveContent);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<ProcessResult> RunQpdfProcessAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        bool scanForActiveContent)
    {
        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return ProcessResult.Unavailable("No se pudo iniciar qpdf.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ProcessResult.Unavailable(ex.Message);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, scanForActiveContent);
        var stderrTask = ReadBoundedAsync(process.StandardError, scanForActiveContent: false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }

            if (cancellationToken.IsCancellationRequested)
                throw;

            var timeoutOut = await stdoutTask;
            var timeoutErr = await stderrTask;
            return ProcessResult.Timeout(CombineDetail(timeoutOut.Text, timeoutErr.Text));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(
            process.ExitCode,
            stdout.Text,
            stderr.Text,
            CombineDetail(stdout.Text, stderr.Text),
            ToolUnavailable: false,
            TimedOut: false,
            DetectedDangerousToken: stdout.DetectedDangerousToken);
    }

    private string ResolveExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.QpdfExecutablePath))
            return Path.GetFullPath(_options.QpdfExecutablePath.Trim().Trim('"'));

        var bundled = Path.Combine(AppContext.BaseDirectory, "qpdf", "bin", "qpdf.exe");
        if (File.Exists(bundled))
            return bundled;

        var bundledFlat = Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");
        return File.Exists(bundledFlat) ? bundledFlat : "qpdf.exe";
    }

    private static async Task CopySnapshotAsync(
        string sourcePath,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
                break;
            if (total > maxBytes - read)
                throw new PdfValidationException(
                    PdfInspectionOutcome.TooLarge,
                    $"El PDF excede el límite de validación configurado ({FormatBytes(maxBytes)}).");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<BoundedReadResult> ReadBoundedAsync(
        StreamReader reader,
        bool scanForActiveContent)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(MaxDiagnosticChars, 4096));
        var tail = string.Empty;
        string? detected = null;
        var maxTokenLength = DangerousPdfJsonTokens.Max(token => token.Length);

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read <= 0)
                break;

            if (scanForActiveContent && detected is null)
            {
                var scanWindow = tail + new string(buffer, 0, read);
                detected = DangerousPdfJsonTokens.FirstOrDefault(token =>
                    scanWindow.Contains(token, StringComparison.Ordinal));
                var keep = Math.Min(maxTokenLength - 1, scanWindow.Length);
                tail = keep > 0 ? scanWindow[^keep..] : string.Empty;
            }

            var remaining = MaxDiagnosticChars - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
        }

        return new BoundedReadResult(output.ToString(), detected);
    }

    private static string? CombineDetail(string stdout, string stderr)
    {
        var combined = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(value => value.Length > 0));
        return combined.Length == 0 ? null : combined;
    }

    private static PdfInspectionResult ToolUnavailable(string? detail) => new(
        PdfInspectionOutcome.ToolUnavailable,
        null,
        "No está disponible el componente de validación PDF. Por seguridad, el archivo no se enviará.",
        detail);

    private static PdfInspectionResult TimedOut(string? detail) => new(
        PdfInspectionOutcome.TimedOut,
        null,
        "La validación del PDF excedió el tiempo permitido. Por seguridad, el archivo no se enviará.",
        detail);

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024d):N0} MB";

    private static void DeleteWorkingDirectory(string stagingRoot, string workingDirectory)
    {
        try
        {
            var root = Path.GetFullPath(stagingRoot)
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

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string? Detail,
        bool ToolUnavailable,
        bool TimedOut,
        string? DetectedDangerousToken)
    {
        public static ProcessResult Unavailable(string detail) =>
            new(-1, string.Empty, string.Empty, detail, ToolUnavailable: true, TimedOut: false, DetectedDangerousToken: null);

        public static ProcessResult Timeout(string? detail) =>
            new(-1, string.Empty, string.Empty, detail, ToolUnavailable: false, TimedOut: true, DetectedDangerousToken: null);
    }

    private sealed record BoundedReadResult(string Text, string? DetectedDangerousToken);
}
