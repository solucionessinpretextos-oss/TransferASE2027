// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/SystemHealthService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de salud operativa.
// Interacción: Calcula estado de DB, storage, message bus y señales para monitoring.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FileXfer.Server.Data;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileXfer.Server.Services
{
    public interface ISystemHealthService
    {
        Task<ServerHealthDto> GetSnapshotAsync(CancellationToken ct);
    }

    public sealed class SystemHealthService : ISystemHealthService
    {
        private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
        private readonly StorageOptions _storageOptions;
        private readonly IAlertDeliveryState _alertDeliveryState;
        private readonly ILogger<SystemHealthService> _log;

        public SystemHealthService(
            IDbContextFactory<FileXferDbContext> dbFactory,
            StorageOptions storageOptions,
            IAlertDeliveryState alertDeliveryState,
            ILogger<SystemHealthService> log)
        {
            _dbFactory = dbFactory;
            _storageOptions = storageOptions;
            _alertDeliveryState = alertDeliveryState;
            _log = log;
        }

        public async Task<ServerHealthDto> GetSnapshotAsync(CancellationToken ct)
        {
            bool databaseOk = true;
            bool storageOk = true;
            bool messageBusOk = true; // por ahora no tenemos bus, lo marcamos OK.
            var alertDelivery = _alertDeliveryState.GetSnapshot();
            var alertingOk = alertDelivery.IsHealthy;

            double cpuUsagePercent = 0;
            double memoryUsagePercent = 0;
            double diskUsagePercent = 0;

            string message = "OK";

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                databaseOk = await db.Database.CanConnectAsync(ct);
                if (!databaseOk)
                {
                    message = "DB no disponible";
                }
            }
            catch (Exception ex)
            {
                databaseOk = false;
                _log.LogWarning(ex, "Error comprobando conectividad de base de datos");
                message = "Error comprobando DB";
            }

            try
            {
                var proc = Process.GetCurrentProcess();
                var upTime = DateTime.Now - proc.StartTime;

                if (upTime.TotalMilliseconds > 0)
                {
                    var totalCpuMs = proc.TotalProcessorTime.TotalMilliseconds;
                    var cpuPercent = (totalCpuMs / (Environment.ProcessorCount * upTime.TotalMilliseconds)) * 100.0;
                    cpuUsagePercent = Math.Clamp(cpuPercent, 0, 100);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo calcular el uso de CPU");
                cpuUsagePercent = 0;
            }

            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ref mem))
                {
                    ulong total = mem.ullTotalPhys;
                    ulong avail = mem.ullAvailPhys;
                    if (total > 0)
                    {
                        var used = total - avail;
                        memoryUsagePercent = (double)used * 100.0 / total;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo calcular el uso de memoria");
                memoryUsagePercent = 0;
            }

            try
            {
                var basePath = _storageOptions.BasePath;

                if (string.IsNullOrWhiteSpace(basePath))
                {
                    storageOk = false;
                    _log.LogWarning("StorageOptions.BasePath está vacío; no se puede calcular uso de disco.");
                }
                else
                {
                    var root = Path.GetPathRoot(basePath);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var drive = new DriveInfo(root);
                        var total = drive.TotalSize;
                        var free = drive.AvailableFreeSpace;
                        if (total > 0)
                        {
                            var used = total - free;
                            diskUsagePercent = (double)used * 100.0 / total;
                        }
                    }
                    else
                    {
                        storageOk = false;
                        _log.LogWarning("No se pudo obtener la raíz del path de almacenamiento: {Path}", basePath);
                    }
                }
            }
            catch (Exception ex)
            {
                storageOk = false;
                _log.LogWarning(ex,
                    "Error calculando uso de disco para StoragePath={Path}",
                    _storageOptions.BasePath);
                diskUsagePercent = 0;
            }

            if (!databaseOk || !storageOk || !messageBusOk || !alertingOk)
            {
                message = "Advertencias: " +
                          (databaseOk ? "" : "[DB] ") +
                          (storageOk ? "" : "[Storage] ") +
                          (messageBusOk ? "" : "[Bus] ") +
                          (alertingOk ? "" : "[Alertas] ");
            }

            var dto = new ServerHealthDto
            {
                IsHealthy = databaseOk && storageOk && messageBusOk && alertingOk,
                Message = message,
                CpuUsagePercent = Math.Round(cpuUsagePercent, 1),
                MemoryUsagePercent = Math.Round(memoryUsagePercent, 1),
                DiskUsagePercent = Math.Round(diskUsagePercent, 1),
                DatabaseOk = databaseOk,
                StorageOk = storageOk,
                MessageBusOk = messageBusOk,
                AlertingOk = alertingOk,
                AlertingEnabled = alertDelivery.Enabled,
                AlertQueuePending = alertDelivery.Pending,
                AlertQueueInFlight = alertDelivery.InFlight,
                AlertQueueCapacity = alertDelivery.Capacity,
                AlertEnqueuedTotal = alertDelivery.EnqueuedTotal,
                AlertDeliveredTotal = alertDelivery.DeliveredTotal,
                AlertDeduplicatedTotal = alertDelivery.DeduplicatedTotal,
                AlertDroppedTotal = alertDelivery.DroppedTotal,
                AlertRetryTotal = alertDelivery.RetryTotal,
                AlertTerminalFailureTotal = alertDelivery.TerminalFailureTotal,
                AlertLastDeliveredUtc = alertDelivery.LastDeliveredUtc,
                AlertLastFailureUtc = alertDelivery.LastFailureUtc
            };

            return dto;
        }

        #region P/Invoke GlobalMemoryStatusEx

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                dwMemoryLoad = 0;
                ullTotalPhys = 0;
                ullAvailPhys = 0;
                ullTotalPageFile = 0;
                ullAvailPageFile = 0;
                ullTotalVirtual = 0;
                ullAvailVirtual = 0;
                ullAvailExtendedVirtual = 0;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        #endregion
    }
}
