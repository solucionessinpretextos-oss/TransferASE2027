using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Pdf;

namespace FileXfer.FileSafety;

public enum FileInspectionOutcome
{
    Valid,
    Encrypted,
    Damaged,
    UnsafeContent,
    Invalid,
    Unsupported,
    TooLarge,
    ToolUnavailable,
    TimedOut
}

public enum FileSafetyStage
{
    DetectingType,
    CreatingSafeCopy,
    CheckingProtection,
    CheckingStructure,
    CheckingContent,
    Repairing,
    Revalidating,
    Ready,
    Rejected
}

public sealed record FileSafetyProgress(
    FileSafetyStage Stage,
    double Percentage,
    string UserMessage);

public sealed record FileInspectionResult(
    FileInspectionOutcome Outcome,
    string Format,
    string UserMessage,
    string? TechnicalDetail = null)
{
    public bool IsAccepted => Outcome == FileInspectionOutcome.Valid;
}

public sealed class FileValidationException : IOException
{
    public FileValidationException(
        FileInspectionOutcome outcome,
        string message,
        string? technicalDetail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Outcome = outcome;
        TechnicalDetail = technicalDetail;
    }

    public FileInspectionOutcome Outcome { get; }

    public string? TechnicalDetail { get; }
}

public sealed class PreparedSafeFile : IAsyncDisposable, IDisposable
{
    private readonly string? _stagingRoot;
    private readonly string? _workingDirectory;
    private readonly PreparedPdfFile? _preparedPdf;
    private int _disposed;

    internal PreparedSafeFile(
        string originalPath,
        string uploadPath,
        string format,
        bool wasRepaired,
        string? stagingRoot,
        string? workingDirectory,
        PreparedPdfFile? preparedPdf = null)
    {
        OriginalPath = originalPath;
        UploadPath = uploadPath;
        Format = format;
        WasRepaired = wasRepaired;
        _stagingRoot = stagingRoot;
        _workingDirectory = workingDirectory;
        _preparedPdf = preparedPdf;
    }

    public string OriginalPath { get; }

    public string UploadPath { get; }

    public string Format { get; }

    public bool WasRepaired { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_preparedPdf is not null)
        {
            _preparedPdf.Dispose();
            return;
        }

        if (string.IsNullOrWhiteSpace(_stagingRoot) || string.IsNullOrWhiteSpace(_workingDirectory))
            return;

        try
        {
            var root = Path.GetFullPath(_stagingRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(_workingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch
        {
            // La limpieza diferida nunca debe convertir una carga válida en fallida.
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IFileSafetyService
{
    Task<FileInspectionResult> InspectAsync(
        string path,
        string originalFileName,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<PreparedSafeFile> PrepareForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<PreparedSafeFile> PrepareSnapshotForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<FileSafetyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
