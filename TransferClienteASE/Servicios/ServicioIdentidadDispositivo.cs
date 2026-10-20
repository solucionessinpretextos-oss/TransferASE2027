using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TransferClienteASE.Servicios
{
    /// <summary>
    /// Mantiene una identidad estable por instalación. No contiene secretos ni credenciales;
    /// únicamente evita que una carga parcial cambie de dueño después de reiniciar el cliente.
    /// </summary>
    public static class ServicioIdentidadDispositivo
    {
        private const string Prefijo = "TRANSFER-ASE-";
        private static readonly object Gate = new();
        private static string? _cached;

        public static string ObtenerOCrear()
        {
            lock (Gate)
            {
                if (EsValida(_cached))
                    return _cached!;

                using var processGate = new Mutex(
                    initiallyOwned: false,
                    name: @"Local\ASE.TransferClienteASE.DeviceIdentity");
                var acquired = false;
                try
                {
                    try
                    {
                        acquired = processGate.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    if (!acquired)
                        throw new IOException("No fue posible bloquear la identidad local del dispositivo.");

                    var directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ASE", "TransferClienteASE");
                    var path = Path.Combine(directory, "device.id");
                    Directory.CreateDirectory(directory);

                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                        if (EsValida(existing))
                        {
                            _cached = existing;
                            return existing;
                        }
                    }

                    var created = Prefijo + Guid.NewGuid().ToString("N");
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, created, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    try
                    {
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(temporary))
                                File.Delete(temporary);
                        }
                        catch
                        {
                        }
                    }

                    _cached = created;
                    return created;
                }
                finally
                {
                    if (acquired)
                        processGate.ReleaseMutex();
                }
            }
        }

        private static bool EsValida(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !value.StartsWith(Prefijo, StringComparison.Ordinal))
            {
                return false;
            }

            return Guid.TryParseExact(value[Prefijo.Length..], "N", out _);
        }
    }
}
