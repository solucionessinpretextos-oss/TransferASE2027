using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Diagnostics;

namespace TransferClienteASE.Servicios
{
    /// <summary>
    /// Materializa, en una carpeta privada del usuario, las herramientas que viajan
    /// incrustadas en la publicación de un solo archivo. La aplicación distribuida
    /// sigue siendo un único EXE; la extracción ocurre de forma automática y
    /// determinista, sin depender de archivos colocados manualmente a su lado.
    /// </summary>
    internal static class EmbeddedRuntimeAssets
    {
        private const string ResourcePrefix = "TransferClienteASE.Embedded.";
        private const long MaxEmbeddedFileBytes = 64L * 1024L * 1024L;
        private const long MaxQpdfExpandedBytes = 64L * 1024L * 1024L;
        private const int MaxQpdfEntries = 128;
        private static readonly object Sync = new();

        private static string RuntimeRoot
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    throw new InvalidOperationException("Windows no proporcionó una carpeta local de aplicación.");

                return Path.Combine(
                    local,
                    "AuditoriaSuperiorDelEstado",
                    "TransferClienteASE",
                    "runtime-assets");
            }
        }

        public static void PrepareQpdfIfEmbedded()
        {
            var executable = TryExtractQpdf();
            if (!string.IsNullOrWhiteSpace(executable))
                Environment.SetEnvironmentVariable("FILEXFER_QPDF_PATH", executable);
        }

        public static string? TryResolveUpdateAsset(string fileName)
        {
            var resourceName = fileName switch
            {
                "TransferClienteASE.Updater.exe" => ResourcePrefix + "TransferClienteASE.Updater.exe",
                "update-public.pem" => ResourcePrefix + "update-public.pem",
                _ => null
            };

            return resourceName is null
                ? null
                : TryExtractFile(resourceName, fileName);
        }

        public static int RunSelfTest()
        {
            try
            {
                PrepareQpdfIfEmbedded();
                var qpdf = Environment.GetEnvironmentVariable("FILEXFER_QPDF_PATH");
                if (string.IsNullOrWhiteSpace(qpdf) || !File.Exists(qpdf))
                    throw new InvalidOperationException("qpdf integrado no quedó disponible.");

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = qpdf,
                    ArgumentList = { "--version" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
                        throw new InvalidOperationException("qpdf integrado no superó su prueba de arranque.");
                }

                var updater = TryResolveUpdateAsset("TransferClienteASE.Updater.exe");
                if (string.IsNullOrWhiteSpace(updater) || !File.Exists(updater))
                    throw new InvalidOperationException("El updater integrado no quedó disponible.");
                using (var stream = File.OpenRead(updater))
                {
                    if (stream.ReadByte() != 0x4D || stream.ReadByte() != 0x5A)
                        throw new InvalidDataException("El updater integrado no tiene cabecera PE.");
                }

                var publicKey = TryResolveUpdateAsset("update-public.pem");
                if (string.IsNullOrWhiteSpace(publicKey) || !File.Exists(publicKey))
                    throw new InvalidOperationException("La llave pública integrada no quedó disponible.");
                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(publicKey));
                if (rsa.KeySize < 3072)
                    throw new CryptographicException("La llave pública integrada es menor de 3072 bits.");

                return 0;
            }
            catch
            {
                return 10;
            }
        }

        private static string? TryExtractQpdf()
        {
            const string resourceName = ResourcePrefix + "qpdf-win-x64.zip";
            using var resource = OpenResource(resourceName);
            if (resource is null)
                return null;

            var archiveBytes = ReadBounded(resource, MaxEmbeddedFileBytes);
            var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            var root = Path.Combine(RuntimeRoot, "qpdf", hash);
            var marker = Path.Combine(root, ".complete");
            var executable = Path.Combine(root, "bin", "qpdf.exe");

            lock (Sync)
            {
                if (File.Exists(marker) && File.Exists(executable) &&
                    string.Equals(File.ReadAllText(marker).Trim(), hash, StringComparison.Ordinal))
                {
                    return executable;
                }

                Directory.CreateDirectory(root);
                RejectReparsePoints(RuntimeRoot, root);
                ExtractQpdfArchive(archiveBytes, root);
                if (!File.Exists(executable))
                    throw new InvalidDataException("El recurso integrado de qpdf no contiene bin/qpdf.exe.");

                WriteAtomically(marker, System.Text.Encoding.ASCII.GetBytes(hash + Environment.NewLine));
                return executable;
            }
        }

        private static string? TryExtractFile(string resourceName, string fileName)
        {
            using var resource = OpenResource(resourceName);
            if (resource is null)
                return null;

            var bytes = ReadBounded(resource, MaxEmbeddedFileBytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var directory = Path.Combine(RuntimeRoot, "update-bootstrap", hash);
            var destination = Path.Combine(directory, fileName);

            lock (Sync)
            {
                Directory.CreateDirectory(directory);
                RejectReparsePoints(RuntimeRoot, directory);

                if (File.Exists(destination))
                {
                    var existingHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)))
                        .ToLowerInvariant();
                    if (string.Equals(existingHash, hash, StringComparison.Ordinal))
                        return destination;
                }

                WriteAtomically(destination, bytes);
                return destination;
            }
        }

        private static Stream? OpenResource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        private static byte[] ReadBounded(Stream input, long maxBytes)
        {
            using var output = new MemoryStream();
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                if (total > maxBytes - read)
                    throw new InvalidDataException("Un recurso integrado excede el límite permitido.");
                output.Write(buffer, 0, read);
                total += read;
            }
            return output.ToArray();
        }

        private static void ExtractQpdfArchive(byte[] archiveBytes, string destinationRoot)
        {
            using var memory = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is <= 0 or > MaxQpdfEntries)
                throw new InvalidDataException("El recurso integrado de qpdf tiene una cantidad inválida de entradas.");

            var root = WithTrailingSeparator(Path.GetFullPath(destinationRoot));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                ValidateArchiveEntry(normalized, entry.ExternalAttributes);
                if (!seen.Add(normalized))
                    throw new InvalidDataException("El recurso integrado de qpdf contiene rutas duplicadas.");

                if (entry.Length < 0 || expanded > MaxQpdfExpandedBytes - entry.Length)
                    throw new InvalidDataException("El recurso integrado de qpdf excede el límite expandido.");
                expanded += entry.Length;

                var destination = Path.GetFullPath(Path.Combine(destinationRoot, normalized));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El recurso integrado de qpdf intenta salir de su carpeta.");

                if (normalized.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                    File.Move(temp, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
        }

        private static void ValidateArchiveEntry(string name, int externalAttributes)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(name) || name.Split('/').Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException("El recurso integrado contiene una ruta insegura.");
            }

            // Unix file type 0120000 representa un enlace simbólico.
            var unixMode = (externalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException("El recurso integrado contiene un enlace simbólico no permitido.");
        }

        private static void WriteAtomically(string destination, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        private static void RejectReparsePoints(string trustedRoot, string path)
        {
            var root = WithTrailingSeparator(Path.GetFullPath(trustedRoot));
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null &&
                   WithTrailingSeparator(current.FullName).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("La carpeta de recursos integrados contiene un enlace no permitido.");
                current = current.Parent;
            }
        }

        private static string WithTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
