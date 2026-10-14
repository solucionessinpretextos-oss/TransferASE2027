using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TransferClienteASE.Updater;

internal static class Program
{
    private const long MaxZipBytes = 512L * 1024L * 1024L;
    private const long MaxExpandedBytes = 1024L * 1024L * 1024L;
    private const int MaxEntries = 20_000;

    private static int Main(string[] args)
    {
        string? logPath = null;

        try
        {
            if (args.Length is not 7 and not 8)
            {
                Console.WriteLine(
                    "Uso: TransferClienteASE.Updater <zip> <destino> <exe> <version> <sha256> <firmaBase64> <clavePublicaPem> [pidCliente|--verify-only]");
                return 1;
            }

            var zipPath = Path.GetFullPath(args[0]);
            var destPath = Path.GetFullPath(args[1]);
            var exeName = args[2];
            var version = args[3];
            var expectedSha256 = args[4];
            var signatureBase64 = args[5];
            var publicKeyPath = Path.GetFullPath(args[6]);
            int? clientProcessId = null;
            var verifyOnly = false;
            if (args.Length == 8)
            {
                if (string.Equals(args[7], "--verify-only", StringComparison.Ordinal))
                {
                    verifyOnly = true;
                }
                else
                {
                    if (!int.TryParse(args[7], out var parsedProcessId) || parsedProcessId <= 0)
                        throw new InvalidOperationException("El PID del cliente no es válido.");
                    clientProcessId = parsedProcessId;
                }
            }

            ValidateInputs(zipPath, destPath, exeName, version, publicKeyPath);
            using var verifiedPackage = VerifyPackageAndOpen(
                zipPath,
                version,
                expectedSha256,
                signatureBase64,
                publicKeyPath);

            if (verifyOnly)
            {
                VerifyPackageContents(verifiedPackage, exeName);
                Console.WriteLine("Paquete firmado y contenido verificados correctamente.");
                return 0;
            }

            if (IsRunningInsideDestination(destPath))
            {
                return RelaunchFromTemp(
                    zipPath,
                    destPath,
                    exeName,
                    version,
                    expectedSha256,
                    signatureBase64,
                    publicKeyPath,
                    clientProcessId);
            }

            var logDir = Path.Combine(Path.GetTempPath(), "TransferClienteASEUpdate", "logs");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, $"updater-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var log = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };

            void L(string message)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                Console.WriteLine(line);
                log.WriteLine(line);
            }

            L("=== TransferClienteASE Updater seguro ===");
            L($"Paquete: {zipPath}");
            L($"Destino: {destPath}");
            L("SHA-256 y firma RSA-PSS verificados.");

            WaitForClientExit(exeName, clientProcessId, L);

            var operationRoot = Path.Combine(
                Path.GetTempPath(),
                "TransferClienteASEUpdate",
                "operations",
                Guid.NewGuid().ToString("N"));
            var stagingRoot = Path.Combine(operationRoot, "staging");
            var backupRoot = Path.Combine(operationRoot, "backup");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(backupRoot);

            try
            {
                L("Validando y extrayendo el ZIP en staging...");
                ExtractSafely(verifiedPackage, stagingRoot);
                var source = DetectExtractionSource(stagingRoot, L);

                L("Aplicando actualización con respaldo transaccional...");
                ApplyWithRollback(source, destPath, backupRoot, L);

                var exePath = Path.Combine(destPath, exeName);
                if (!File.Exists(exePath))
                    throw new FileNotFoundException("El paquete no dejó disponible el ejecutable principal.", exePath);

                L("Actualización aplicada. Iniciando cliente...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = destPath
                });

                DeleteTemporaryDirectory(operationRoot);
                L("Updater finalizó correctamente.");
                return 0;
            }
            catch
            {
                // ApplyWithRollback restaura los archivos. Se conserva operationRoot como
                // evidencia diagnóstica si la aplicación falló después de la copia.
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error en updater: " + ex.Message);
            try
            {
                if (!string.IsNullOrWhiteSpace(logPath))
                    File.AppendAllText(logPath, Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
            }
            catch
            {
            }

            return 2;
        }
    }

    private static void ValidateInputs(
        string zipPath,
        string destPath,
        string exeName,
        string version,
        string publicKeyPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("No se encontró el paquete ZIP.", zipPath);
        if (new FileInfo(zipPath).Length is <= 0 or > MaxZipBytes)
            throw new InvalidDataException("El paquete ZIP está vacío o excede el límite permitido.");
        if (!Directory.Exists(destPath))
            throw new DirectoryNotFoundException("No existe el directorio destino.");
        if (string.Equals(destPath.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(destPath)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La raíz de una unidad no puede ser destino de actualización.");
        if (!string.Equals(exeName, Path.GetFileName(exeName), StringComparison.Ordinal) ||
            !Path.GetExtension(exeName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El nombre del ejecutable principal no es válido.");
        if (!Version.TryParse(version, out _) || version.Contains('\n') || version.Contains('\r'))
            throw new InvalidOperationException("La versión del paquete no es válida.");
        RejectReparsePoint(new DirectoryInfo(destPath));
        if (!File.Exists(publicKeyPath))
            throw new FileNotFoundException("No se encontró la clave pública de actualización.", publicKeyPath);
    }

    private static FileStream VerifyPackageAndOpen(
        string zipPath,
        string version,
        string expectedSha256,
        string signatureBase64,
        string publicKeyPath)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
            throw new InvalidDataException("El SHA-256 esperado no es válido.");

        byte[] expected;
        byte[] signature;
        try
        {
            expected = Convert.FromHexString(expectedSha256);
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("El hash o la firma no tiene un formato válido.", ex);
        }

        var stream = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new CryptographicException("El paquete no coincide con el SHA-256 esperado.");

            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
            if (rsa.KeySize < 2048)
                throw new CryptographicException("La clave pública de actualización es demasiado corta.");
            var canonical = string.Join("\n", new[]
            {
                "FILEXFER-UPDATE-V1",
                "transfer-cliente-ase",
                version,
                expectedSha256.ToLowerInvariant()
            });
            if (!rsa.VerifyData(
                    Encoding.UTF8.GetBytes(canonical),
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new CryptographicException("La firma RSA-PSS del paquete no es válida.");

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ExtractSafely(Stream verifiedPackage, string stagingRoot)
    {
        var root = WithTrailingSeparator(Path.GetFullPath(stagingRoot));
        long totalExpanded = 0;
        var entries = 0;

        verifiedPackage.Position = 0;
        using var archive = new ZipArchive(verifiedPackage, ZipArchiveMode.Read, leaveOpen: true);
        long actualExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            entries++;
            if (entries > MaxEntries)
                throw new InvalidDataException("El ZIP contiene demasiadas entradas.");

            if (entry.Length < 0 || totalExpanded > MaxExpandedBytes - entry.Length)
                throw new InvalidDataException("El ZIP excede el tamaño máximo expandido.");
            totalExpanded += entry.Length;

            ValidateArchiveEntryName(entry.FullName);

            var target = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));
            if (!WithTrailingSeparator(target).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El ZIP contiene una ruta que sale de staging.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            long entryExpanded = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                if (actualExpanded > MaxExpandedBytes - read)
                    throw new InvalidDataException("El ZIP excede el tamaño máximo expandido real.");

                actualExpanded += read;
                entryExpanded += read;
                if (entryExpanded > entry.Length)
                    throw new InvalidDataException("El tamaño expandido de una entrada no coincide con el manifiesto ZIP.");

                output.Write(buffer, 0, read);
            }

            if (entryExpanded != entry.Length)
                throw new InvalidDataException("Una entrada ZIP quedó truncada durante la extracción.");
        }
    }

    private static void VerifyPackageContents(Stream verifiedPackage, string exeName)
    {
        var verificationRoot = Path.Combine(
            Path.GetTempPath(),
            "TransferClienteASEUpdate",
            "verification",
            Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.Combine(verificationRoot, "staging");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            ExtractSafely(verifiedPackage, stagingRoot);
            var source = DetectExtractionSource(stagingRoot, _ => { });
            var executable = Path.Combine(source, exeName);
            if (!File.Exists(executable))
                throw new InvalidDataException("El paquete no contiene el ejecutable principal esperado.");

            var updater = Path.Combine(source, "TransferClienteASE.Updater.exe");
            if (!File.Exists(updater))
                throw new InvalidDataException("El paquete no contiene el actualizador seguro.");

            var publicKey = Path.Combine(source, "update-public.pem");
            if (!File.Exists(publicKey))
                throw new InvalidDataException("El paquete no contiene la clave pública de actualización.");
        }
        finally
        {
            DeleteTemporaryDirectory(verificationRoot);
        }
    }

    private static void ApplyWithRollback(
        string sourceDir,
        string destinationDir,
        string backupRoot,
        Action<string> log)
    {
        var destinationRoot = WithTrailingSeparator(Path.GetFullPath(destinationDir));
        var backups = new List<(string Backup, string Destination)>();
        var created = new List<string>();

        try
        {
            foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, sourceFile);
                var destination = Path.GetFullPath(Path.Combine(destinationDir, relative));
                if (!WithTrailingSeparator(destination).StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("El paquete intenta escribir fuera de la instalación.");

                var destinationDirectory = Path.GetDirectoryName(destination)!;
                EnsureSafeDestinationPath(destinationDir, destinationDirectory);
                Directory.CreateDirectory(destinationDirectory);

                if (File.Exists(destination))
                    RejectReparsePoint(new FileInfo(destination));

                if (File.Exists(destination))
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    backups.Add((backup, destination));
                }
                else
                {
                    created.Add(destination);
                }

                CopyWithRetry(sourceFile, destination);
                log($"Actualizado: {relative}");
            }
        }
        catch (Exception applyError)
        {
            log("Falló la copia. Iniciando rollback...");
            var rollbackErrors = new List<Exception>();

            foreach (var path in created.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }

            foreach (var item in backups.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(item.Backup, item.Destination, overwrite: true);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }

            if (rollbackErrors.Count > 0)
                throw new AggregateException("La actualización falló y el rollback quedó incompleto.", new[] { applyError }.Concat(rollbackErrors));

            throw new InvalidOperationException("La actualización falló; la instalación anterior fue restaurada.", applyError);
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 25; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }

        throw new IOException($"No se pudo reemplazar {Path.GetFileName(destination)}.", last);
    }

    private static void WaitForClientExit(string exeName, int? clientProcessId, Action<string> log)
    {
        if (clientProcessId.HasValue)
        {
            for (var attempt = 1; attempt <= 90; attempt++)
            {
                try
                {
                    using var client = Process.GetProcessById(clientProcessId.Value);
                    if (client.HasExited)
                        return;

                    log($"Cliente aún activo. Esperando {attempt}/90...");
                    Thread.Sleep(1000);
                }
                catch (ArgumentException)
                {
                    Thread.Sleep(800);
                    return;
                }
            }

            throw new TimeoutException("El cliente no se cerró dentro del tiempo permitido; no se modificó la instalación.");
        }

        var processName = Path.GetFileNameWithoutExtension(exeName);
        for (var attempt = 1; attempt <= 90; attempt++)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                Thread.Sleep(800);
                return;
            }

            foreach (var process in processes)
                process.Dispose();

            log($"Cliente aún activo. Esperando {attempt}/90...");
            Thread.Sleep(1000);
        }

        throw new TimeoutException("El cliente no se cerró dentro del tiempo permitido; no se modificó la instalación.");
    }

    private static bool IsRunningInsideDestination(string destination)
    {
        var baseDirectory = WithTrailingSeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var destinationRoot = WithTrailingSeparator(Path.GetFullPath(destination));
        return baseDirectory.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int RelaunchFromTemp(
        string zipPath,
        string destination,
        string exeName,
        string version,
        string sha256,
        string signatureBase64,
        string publicKeyPath,
        int? clientProcessId)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("No se pudo localizar el updater actual.");

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "TransferClienteASEUpdate",
            "updater-run",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var updaterBaseName = Path.GetFileNameWithoutExtension(currentExe);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);
            if (string.Equals(source, currentExe, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(updaterBaseName + ".", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, Path.Combine(tempDir, name), overwrite: false);
            }
        }

        var tempExe = Path.Combine(tempDir, Path.GetFileName(currentExe));
        if (!File.Exists(tempExe))
            throw new FileNotFoundException("No se pudo aislar el updater para ejecutar la actualización.", tempExe);
        var startInfo = new ProcessStartInfo
        {
            FileName = tempExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir
        };
        startInfo.ArgumentList.Add(zipPath);
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add(exeName);
        startInfo.ArgumentList.Add(version);
        startInfo.ArgumentList.Add(sha256);
        startInfo.ArgumentList.Add(signatureBase64);
        startInfo.ArgumentList.Add(publicKeyPath);
        if (clientProcessId.HasValue)
            startInfo.ArgumentList.Add(clientProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Process.Start(startInfo);
        return 0;
    }

    private static string DetectExtractionSource(string stagingRoot, Action<string> log)
    {
        var directories = Directory.GetDirectories(stagingRoot);
        var files = Directory.GetFiles(stagingRoot);
        if (files.Length == 0 && directories.Length == 1)
        {
            log($"Se usará la carpeta raíz única: {Path.GetFileName(directories[0])}");
            return directories[0];
        }

        return stagingRoot;
    }

    private static string WithTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static void ValidateArchiveEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            entryName.StartsWith('/') ||
            entryName.StartsWith('\\'))
        {
            throw new InvalidDataException("El ZIP contiene una ruta absoluta o vacía.");
        }

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.IndexOfAny(invalid) >= 0)
                throw new InvalidDataException("El ZIP contiene un nombre de entrada no permitido.");

            var deviceName = Path.GetFileNameWithoutExtension(segment).ToUpperInvariant();
            if (deviceName is "CON" or "PRN" or "AUX" or "NUL" ||
                deviceName.StartsWith("COM", StringComparison.Ordinal) && deviceName.Length == 4 && char.IsDigit(deviceName[3]) ||
                deviceName.StartsWith("LPT", StringComparison.Ordinal) && deviceName.Length == 4 && char.IsDigit(deviceName[3]))
            {
                throw new InvalidDataException("El ZIP contiene un nombre de dispositivo reservado.");
            }
        }
    }

    private static void EnsureSafeDestinationPath(string destinationRoot, string directory)
    {
        var root = WithTrailingSeparator(Path.GetFullPath(destinationRoot));
        var current = new DirectoryInfo(Path.GetFullPath(directory));

        while (current is not null &&
               WithTrailingSeparator(current.FullName).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists)
                RejectReparsePoint(current);

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                              destinationRoot.TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
                break;

            current = current.Parent;
        }
    }

    private static void RejectReparsePoint(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La actualización no puede atravesar vínculos o puntos de reanálisis.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var allowedRoot = WithTrailingSeparator(Path.Combine(Path.GetTempPath(), "TransferClienteASEUpdate"));
        if (WithTrailingSeparator(fullPath).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
