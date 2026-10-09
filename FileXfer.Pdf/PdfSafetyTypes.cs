using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileXfer.Pdf;

public enum PdfInspectionOutcome
{
    Valid,
    Encrypted,
    Damaged,
    UnsafeActiveContent,
    Invalid,
    TooLarge,
    ToolUnavailable,
    TimedOut
}

public sealed record PdfInspectionResult(
    PdfInspectionOutcome Outcome,
    int? PageCount,
    string UserMessage,
    string? TechnicalDetail = null)
{
    public bool IsAccepted => Outcome == PdfInspectionOutcome.Valid;
}

public sealed class PdfValidationException : IOException
{
    public PdfValidationException(PdfInspectionOutcome outcome, string message, string? technicalDetail = null)
        : base(message)
    {
        Outcome = outcome;
        TechnicalDetail = technicalDetail;
    }

    public PdfInspectionOutcome Outcome { get; }

    public string? TechnicalDetail { get; }
}

public sealed class PreparedPdfFile : IAsyncDisposable, IDisposable
{
    private readonly string _stagingRoot;
    private readonly string _workingDirectory;
    private int _disposed;

    internal PreparedPdfFile(
        string originalPath,
        string uploadPath,
        string stagingRoot,
        string workingDirectory,
        bool wasRepaired,
        int pageCount)
    {
        OriginalPath = originalPath;
        UploadPath = uploadPath;
        _stagingRoot = stagingRoot;
        _workingDirectory = workingDirectory;
        WasRepaired = wasRepaired;
        PageCount = pageCount;
    }

    public string OriginalPath { get; }

    public string UploadPath { get; }

    public bool WasRepaired { get; }

    public int PageCount { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            var root = Path.GetFullPath(_stagingRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(_workingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_workingDirectory))
                Directory.Delete(_workingDirectory, recursive: true);
        }
        catch
        {
            // La limpieza diferida de staging no debe convertir una carga válida en fallida.
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IPdfSafetyService
{
    Task<PdfInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default);

    Task<PreparedPdfFile> PrepareForUploadAsync(
        string originalPath,
        string stagingRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
