using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileXfer.FileSafety;

var root = Path.Combine(Path.GetTempPath(), "FileXfer.FileSafety.SmokeTests", Guid.NewGuid().ToString("N"));
var staging = Path.Combine(root, "staging");
Directory.CreateDirectory(root);

try
{
    var service = new DefaultFileSafetyService();

    var validDocx = Path.Combine(root, "valido.docx");
    WriteDocx(validDocx, externalRelationship: false, includeMacro: false);
    var validDocxResult = await service.InspectAsync(validDocx, Path.GetFileName(validDocx));
    Require(validDocxResult.IsAccepted, $"DOCX válido rechazado: {validDocxResult.Outcome} / {validDocxResult.UserMessage}");

    var externalDocx = Path.Combine(root, "externo.docx");
    WriteDocx(externalDocx, externalRelationship: true, includeMacro: false);
    var externalResult = await service.InspectAsync(externalDocx, Path.GetFileName(externalDocx));
    Require(!externalResult.IsAccepted, "DOCX con relación externa fue aceptado.");

    var macroDocx = Path.Combine(root, "macro.docx");
    WriteDocx(macroDocx, externalRelationship: false, includeMacro: true);
    var macroResult = await service.InspectAsync(macroDocx, Path.GetFileName(macroDocx));
    Require(macroResult.Outcome == FileInspectionOutcome.UnsafeContent, "DOCX con macro no fue bloqueado como contenido inseguro.");

    var protectedDocx = Path.Combine(root, "protegido.docx");
    await File.WriteAllBytesAsync(protectedDocx, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
    var protectedResult = await service.InspectAsync(protectedDocx, Path.GetFileName(protectedDocx));
    Require(protectedResult.Outcome == FileInspectionOutcome.Encrypted, "OOXML protegido no fue identificado como cifrado/protegido.");

    var validJson = Path.Combine(root, "valido.json");
    await File.WriteAllTextAsync(validJson, "{\"resultado\":true,\"elementos\":[1,2,3]}", new UTF8Encoding(false, true));
    Require((await service.InspectAsync(validJson, Path.GetFileName(validJson))).IsAccepted, "JSON válido fue rechazado.");

    var brokenJson = Path.Combine(root, "roto.json");
    await File.WriteAllTextAsync(brokenJson, "{\"resultado\":", new UTF8Encoding(false, true));
    Require((await service.InspectAsync(brokenJson, Path.GetFileName(brokenJson))).Outcome == FileInspectionOutcome.Damaged,
        "JSON truncado no fue detectado como dañado.");

    var unsafeSvg = Path.Combine(root, "inseguro.svg");
    await File.WriteAllTextAsync(unsafeSvg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
    Require((await service.InspectAsync(unsafeSvg, Path.GetFileName(unsafeSvg))).Outcome == FileInspectionOutcome.UnsafeContent,
        "SVG con script no fue bloqueado.");

    var validPng = Path.Combine(root, "pixel.png");
    await File.WriteAllBytesAsync(
        validPng,
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    Require((await service.InspectAsync(validPng, Path.GetFileName(validPng))).IsAccepted,
        "PNG válido fue rechazado.");

    var brokenPng = Path.Combine(root, "pixel-roto.png");
    var brokenPngBytes = await File.ReadAllBytesAsync(validPng);
    brokenPngBytes[brokenPngBytes.Length / 2] ^= 0x01;
    await File.WriteAllBytesAsync(brokenPng, brokenPngBytes);
    Require((await service.InspectAsync(brokenPng, Path.GetFileName(brokenPng))).Outcome == FileInspectionOutcome.Damaged,
        "PNG con CRC alterado no fue detectado como dañado.");

    var text = Path.Combine(root, "informe.txt");
    await File.WriteAllTextAsync(text, "Informe de auditoría\r\nLínea 2", new UTF8Encoding(false, true));
    var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(text));
    var progressItems = new List<FileSafetyProgress>();
    var prepared = await service.PrepareForUploadAsync(text, staging, new InlineProgress<FileSafetyProgress>(progressItems.Add));
    var preparedDirectory = Path.GetDirectoryName(prepared.UploadPath)!;
    Require(!string.Equals(prepared.UploadPath, text, StringComparison.OrdinalIgnoreCase), "La preparación no creó una copia aislada.");
    Require(progressItems.Any(item => item.Stage == FileSafetyStage.CreatingSafeCopy), "No se informó la creación de la copia segura.");
    Require(progressItems.Any(item => item.Stage == FileSafetyStage.Ready && item.Percentage == 100), "No se informó el 100% de la inspección.");
    Require(progressItems.Select(item => item.Percentage).SequenceEqual(progressItems.Select(item => item.Percentage).OrderBy(value => value)),
        "El porcentaje de inspección retrocedió.");
    await prepared.DisposeAsync();
    Require(!Directory.Exists(preparedDirectory), "La copia temporal no se eliminó.");
    Require(SHA256.HashData(await File.ReadAllBytesAsync(text)).SequenceEqual(originalHash), "La preparación modificó el archivo original.");

    var legacyDoc = Path.Combine(root, "legado.doc");
    await File.WriteAllBytesAsync(legacyDoc, new byte[] { 1, 2, 3, 4 });
    Require((await service.InspectAsync(legacyDoc, Path.GetFileName(legacyDoc))).Outcome == FileInspectionOutcome.Unsupported,
        "Un formato legado no verificable fue aceptado.");

    Console.WriteLine("PASS package=valid encrypted=blocked macro=blocked external=blocked");
    Console.WriteLine("PASS structured=json-valid json-damaged svg-active-blocked image-crc-validated");
    Console.WriteLine("PASS preflight=isolated progress-monotonic cleanup-complete original-unchanged");
    Console.WriteLine("PASS unsupported=fail-closed");
    return 0;
}
finally
{
    try
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
    catch
    {
    }
}

static void WriteDocx(string path, bool externalRelationship, bool includeMacro)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(archive, "[Content_Types].xml",
        "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
    WriteEntry(archive, "_rels/.rels", externalRelationship
        ? "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"https://example.invalid/payload\" TargetMode=\"External\"/></Relationships>"
        : "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"word/document.xml\"/></Relationships>");
    WriteEntry(archive, "word/document.xml",
        "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
    if (includeMacro)
        WriteEntry(archive, "word/vbaProject.bin", "macro");
}

static void WriteEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false, true));
    writer.Write(content);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
