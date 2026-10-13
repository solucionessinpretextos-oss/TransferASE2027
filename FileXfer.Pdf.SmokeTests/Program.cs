using System.Diagnostics;
using System.Security.Cryptography;
using FileXfer.Pdf;

if (args.Length is not (1 or 5))
{
    Console.Error.WriteLine(
        "Uso: smoke <qpdf.exe> | smoke <valid.pdf> <encrypted.pdf> <repairable.pdf> <active-content.pdf> <qpdf.exe>");
    return 2;
}

var stagingRoot = Path.Combine(Path.GetTempPath(), "FileXfer.Pdf.SmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stagingRoot);
var qpdfPath = Path.GetFullPath(args[^1]);
string validPath;
string encryptedPath;
string repairablePath;
string activeContentPath;

if (args.Length == 1)
{
    validPath = Path.Combine(stagingRoot, "valid.pdf");
    encryptedPath = Path.Combine(stagingRoot, "encrypted.pdf");
    repairablePath = Path.Combine(stagingRoot, "repairable.pdf");
    activeContentPath = Path.Combine(stagingRoot, "active-content.pdf");
    WriteSimplePdf(validPath, corruptStartXref: false, activeContent: false);
    WriteSimplePdf(repairablePath, corruptStartXref: true, activeContent: false);
    WriteSimplePdf(activeContentPath, corruptStartXref: false, activeContent: true);
    await WriteEncryptedPdfAsync(qpdfPath, validPath, encryptedPath);
}
else
{
    validPath = Path.GetFullPath(args[0]);
    encryptedPath = Path.GetFullPath(args[1]);
    repairablePath = Path.GetFullPath(args[2]);
    activeContentPath = Path.GetFullPath(args[3]);
}

var attachmentPath = Path.Combine(stagingRoot, "attachment-annotation.pdf");
WriteAttachmentAnnotationPdf(attachmentPath);

var service = new QpdfPdfSafetyService(new PdfSafetyOptions
{
    QpdfExecutablePath = qpdfPath,
    MaxPdfBytes = 64L * 1024L * 1024L,
    ProcessTimeout = TimeSpan.FromSeconds(30)
});

try
{
    var valid = await service.InspectAsync(validPath);
    Require(valid.Outcome == PdfInspectionOutcome.Valid && valid.PageCount == 1,
        $"El PDF válido no fue aceptado: {valid.Outcome} / {valid.TechnicalDetail}");

    var encrypted = await service.InspectAsync(encryptedPath);
    Require(encrypted.Outcome == PdfInspectionOutcome.Encrypted,
        $"El PDF cifrado no fue rechazado como cifrado: {encrypted.Outcome}");

    var encryptedMessages = new List<string>();
    try
    {
        var unexpected = await service.PrepareForUploadAsync(
            encryptedPath,
            stagingRoot,
            new InlineProgress<string>(encryptedMessages.Add));
        await unexpected.DisposeAsync();
        throw new InvalidOperationException("El PDF cifrado llegó a la ruta de envío.");
    }
    catch (PdfValidationException ex)
    {
        Require(ex.Outcome == PdfInspectionOutcome.Encrypted,
            $"La preparación devolvió un motivo incorrecto para el PDF cifrado: {ex.Outcome}");
        Require(encryptedMessages.Any(message =>
                message.Contains("cifrado", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("contraseña", StringComparison.OrdinalIgnoreCase)),
            "No se mostró al usuario el aviso de cifrado o contraseña.");
    }

    var activeContent = await service.InspectAsync(activeContentPath);
    Require(activeContent.Outcome == PdfInspectionOutcome.UnsafeActiveContent,
        $"El PDF con JavaScript/OpenAction no fue rechazado: {activeContent.Outcome}");

    var attachment = await service.InspectAsync(attachmentPath);
    Require(attachment.Outcome == PdfInspectionOutcome.UnsafeActiveContent,
        $"El PDF con adjunto por anotación no fue rechazado: {attachment.Outcome} / {attachment.TechnicalDetail}");

    var damaged = await service.InspectAsync(repairablePath);
    Require(damaged.Outcome == PdfInspectionOutcome.Damaged,
        $"El PDF de control no fue detectado como dañado: {damaged.Outcome} / {damaged.TechnicalDetail}");

    var originalHash = Hash(repairablePath);
    var messages = new List<string>();
    var progress = new InlineProgress<string>(messages.Add);

    var prepared = await service.PrepareForUploadAsync(
        repairablePath,
        stagingRoot,
        progress);
    var preparedDirectory = Path.GetDirectoryName(prepared.UploadPath)!;

    Require(prepared.WasRepaired, "El PDF dañado no recorrió la ruta de reparación.");
    Require(messages.Any(message => message.Contains("intentando reparar", StringComparison.OrdinalIgnoreCase)),
        "No se notificó al usuario que la reparación estaba en curso.");
    Require(messages.Any(message => message.Contains("PDF reparado", StringComparison.OrdinalIgnoreCase)),
        "No se notificó al usuario que la reparación terminó.");

    var repaired = await service.InspectAsync(prepared.UploadPath);
    Require(repaired.Outcome == PdfInspectionOutcome.Valid && repaired.PageCount == 1,
        $"La copia reparada no superó la segunda validación: {repaired.Outcome}");

    await prepared.DisposeAsync();

    Require(!Directory.Exists(preparedDirectory), "La carpeta temporal no fue eliminada después de la prueba.");
    Require(Hash(repairablePath).SequenceEqual(originalHash), "El PDF original fue modificado durante la reparación.");

    Console.WriteLine("PASS valid=accepted pages=1");
    Console.WriteLine("PASS encrypted=rejected");
    Console.WriteLine("PASS active-content=rejected");
    Console.WriteLine("PASS attachment-annotation=rejected");
    Console.WriteLine("PASS damaged=detected repaired=revalidated original=unchanged staging=cleaned");
    return 0;
}
finally
{
    try
    {
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
    }
    catch
    {
    }
}

static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

static async Task WriteEncryptedPdfAsync(string qpdfPath, string sourcePath, string destinationPath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = qpdfPath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--encrypt");
    startInfo.ArgumentList.Add("smoke-user");
    startInfo.ArgumentList.Add("smoke-owner");
    startInfo.ArgumentList.Add("256");
    startInfo.ArgumentList.Add("--");
    startInfo.ArgumentList.Add(sourcePath);
    startInfo.ArgumentList.Add(destinationPath);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("No se pudo iniciar qpdf para generar el PDF cifrado de prueba.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0 || !File.Exists(destinationPath))
    {
        throw new InvalidOperationException(
            $"qpdf no generó el PDF cifrado de prueba. Exit={process.ExitCode}; {(await stdout)} {(await stderr)}");
    }
}

static void WriteSimplePdf(string path, bool corruptStartXref, bool activeContent)
{
    var objects = activeContent
        ? new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Action /S /JavaScript /JS (app.alert\\(test\\)) >>"
        }
        : new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };

    var bytes = new List<byte>();
    AppendAscii(bytes, "%PDF-1.4\n%1234\n");
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(bytes.Count);
        AppendAscii(bytes, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
    }

    var xrefOffset = bytes.Count;
    AppendAscii(bytes, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    for (var index = 1; index < offsets.Count; index++)
        AppendAscii(bytes, $"{offsets[index]:D10} 00000 n \n");
    var declaredXrefOffset = corruptStartXref ? xrefOffset + 17 : xrefOffset;
    AppendAscii(bytes, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{declaredXrefOffset}\n%%EOF\n");
    File.WriteAllBytes(path, bytes.ToArray());
}

static void WriteAttachmentAnnotationPdf(string path)
{
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [4 0 R] /Contents 7 0 R >>",
        "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS 5 0 R >>",
        "<< /Type /Filespec /F (payload.txt) /UF (payload.txt) /EF << /F 6 0 R /UF 6 0 R >> >>",
        "<< /Type /EmbeddedFile /Length 5 >>\nstream\nhello\nendstream",
        "<< /Length 0 >>\nstream\n\nendstream"
    };

    var bytes = new List<byte>();
    AppendAscii(bytes, "%PDF-1.4\n%1234\n");
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(bytes.Count);
        AppendAscii(bytes, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
    }

    var xrefOffset = bytes.Count;
    AppendAscii(bytes, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    for (var index = 1; index < offsets.Count; index++)
        AppendAscii(bytes, $"{offsets[index]:D10} 00000 n \n");
    AppendAscii(bytes, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
    File.WriteAllBytes(path, bytes.ToArray());
}

static void AppendAscii(List<byte> target, string value) =>
    target.AddRange(System.Text.Encoding.ASCII.GetBytes(value));

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
