using System.Security.Cryptography;
using FileXfer.Pdf;

if (args.Length != 5)
{
    Console.Error.WriteLine("Uso: smoke <valid.pdf> <encrypted.pdf> <repairable.pdf> <active-content.pdf> <qpdf.exe>");
    return 2;
}

var validPath = Path.GetFullPath(args[0]);
var encryptedPath = Path.GetFullPath(args[1]);
var repairablePath = Path.GetFullPath(args[2]);
var activeContentPath = Path.GetFullPath(args[3]);
var qpdfPath = Path.GetFullPath(args[4]);
var stagingRoot = Path.Combine(Path.GetTempPath(), "FileXfer.Pdf.SmokeTests", Guid.NewGuid().ToString("N"));

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

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
