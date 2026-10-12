using System;
using System.Collections.Generic;
using System.IO;

namespace FileXfer.Server.Uploading;

public static class UploadFileNamePolicy
{
    private static readonly HashSet<string> BlockedExtensions = new(
        new[]
        {
            ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd",
            ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".jar", ".lnk", ".url", ".reg", ".chm", ".appx", ".msix",
            ".key", ".pem", ".pfx", ".p12", ".ppk", ".jks", ".keystore",
            ".kdbx", ".env", ".der"
        },
        StringComparer.OrdinalIgnoreCase);

    public static bool TryValidate(string? fileName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "El nombre del archivo no es válido.";
            return false;
        }

        // FileAssembler aplica exactamente estas transformaciones al crear la ruta.
        // Si cambiaran el nombre, se rechaza: la política debe evaluar los mismos bytes
        // de nombre que terminarán almacenados, nunca una representación previa.
        var canonical = fileName.Trim().TrimEnd(' ', '.');
        if (!string.Equals(canonical, fileName, StringComparison.Ordinal))
        {
            error = "El nombre del archivo no puede iniciar o terminar con espacios ni terminar con punto.";
            return false;
        }

        var extension = Path.GetExtension(canonical);
        if (string.IsNullOrWhiteSpace(extension))
        {
            error = "El archivo debe tener una extensión.";
            return false;
        }

        if (BlockedExtensions.Contains(extension))
        {
            error = "El tipo de archivo está bloqueado por la política de seguridad.";
            return false;
        }

        return true;
    }
}
