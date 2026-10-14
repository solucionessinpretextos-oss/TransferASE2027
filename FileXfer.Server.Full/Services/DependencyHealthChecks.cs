using FileXfer.Server.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FileXfer.Server.Services;

/// <summary>
/// Comprueba que la base transaccional propia de FileXfer acepte conexiones.
/// No consulta datos de negocio ni expone detalles de la excepción al consumidor.
/// </summary>
public sealed class PrimaryStoreHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;

    public PrimaryStoreHealthCheck(IDbContextFactory<FileXferDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Primary store unavailable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Primary store unavailable.", ex);
        }
    }
}

/// <summary>
/// Comprueba la conectividad de solo lectura con BuDi mediante una consulta constante.
/// Las credenciales y mensajes del proveedor nunca forman parte de la respuesta HTTP.
/// </summary>
public sealed class BusinessStoreHealthCheck : IHealthCheck
{
    private readonly string? _connectionString;

    public BusinessStoreHealthCheck(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("BudiDev");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return HealthCheckResult.Unhealthy("Business store is not configured.");

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Business store unavailable.", ex);
        }
    }
}
