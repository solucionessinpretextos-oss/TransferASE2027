namespace FileXfer.Server.Uploading;

public sealed class ResumableUploadCleanupService : BackgroundService
{
    private readonly PendingTransferMaintenanceService _maintenance;
    private readonly ILogger<ResumableUploadCleanupService> _logger;

    public ResumableUploadCleanupService(
        PendingTransferMaintenanceService maintenance,
        ILogger<ResumableUploadCleanupService> logger)
    {
        _maintenance = maintenance;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var result = await _maintenance.CleanupExpiredAsync("system", null, stoppingToken);
                if (result.Removed > 0 || result.Failed > 0)
                    _logger.LogInformation("Limpieza automática de pendientes: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Falló la limpieza de cargas reanudables vencidas.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

}
