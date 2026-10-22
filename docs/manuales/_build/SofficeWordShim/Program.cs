using System.Runtime.InteropServices;

static string? ValueAfter(string[] args, string option)
{
    var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var format = ValueAfter(args, "--convert-to");
var outputDirectory = ValueAfter(args, "--outdir");
var input = args.LastOrDefault(File.Exists);

if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase) ||
    string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(input))
{
    Console.Error.WriteLine("SofficeWordShim solo admite --convert-to pdf --outdir <carpeta> <archivo.docx>.");
    return 2;
}

Directory.CreateDirectory(outputDirectory);
var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + ".pdf");
dynamic? word = null;
dynamic? document = null;

try
{
    var wordType = Type.GetTypeFromProgID("Word.Application")
        ?? throw new InvalidOperationException("Microsoft Word no está registrado en el equipo.");
    word = Activator.CreateInstance(wordType)
        ?? throw new InvalidOperationException("No fue posible iniciar Microsoft Word.");
    word.Visible = false;
    word.DisplayAlerts = 0;
    document = word.Documents.Open(Path.GetFullPath(input), ReadOnly: true, Visible: false);
    document.ExportAsFixedFormat(Path.GetFullPath(output), 17);
    Console.WriteLine($"convert {input} -> {output}");
    return File.Exists(output) && new FileInfo(output).Length > 0 ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
finally
{
    try { document?.Close(false); } catch { }
    try { word?.Quit(); } catch { }
    if (document is not null && Marshal.IsComObject(document)) Marshal.FinalReleaseComObject(document);
    if (word is not null && Marshal.IsComObject(word)) Marshal.FinalReleaseComObject(word);
}
