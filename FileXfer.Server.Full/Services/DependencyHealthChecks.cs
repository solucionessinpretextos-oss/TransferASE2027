using FileXfer.Server.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FileXfer.Server.Services;

public sealed record BudiSchemaStatus(
    bool TableExists,
    bool MovimientoIdExists,
    bool UploadIdExists,
    bool UploadIdUniqueIndexExists,
    bool ResultContextIndexExists)
{
    public bool IsReady =>
        TableExists &&
        MovimientoIdExists &&
        UploadIdExists &&
        UploadIdUniqueIndexExists &&
        ResultContextIndexExists;
}

public static class BudiSchemaRequirements
{
    private const string ReadStatusSql = """
        SELECT
            CASE WHEN OBJECT_ID(N'dbo.Transfer_ArchivosLocales', N'U') IS NULL THEN 0 ELSE 1 END AS TableExists,
            CASE WHEN EXISTS (
                SELECT 1
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                  AND c.name = N'MovimientoId'
                  AND t.name = N'int'
            ) THEN 1 ELSE 0 END AS MovimientoIdExists,
            CASE WHEN EXISTS (
                SELECT 1
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                  AND c.name = N'ID_Upload'
                  AND t.name = N'uniqueidentifier'
            ) THEN 1 ELSE 0 END AS UploadIdExists,
            CASE WHEN EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                  AND name = N'UX_Transfer_ArchivosLocales_ID_Upload'
                  AND is_unique = 1
                  AND has_filter = 1
                  AND is_disabled = 0
                  AND REPLACE(REPLACE(REPLACE(UPPER(filter_definition), N'[', N''), N']', N''), N' ', N'')
                      IN (N'(ID_UPLOADISNOTNULL)', N'ID_UPLOADISNOTNULL')
                  AND 1 = (
                      SELECT COUNT(*)
                      FROM sys.index_columns ic
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal > 0
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal = 1
                        AND c.name = N'ID_Upload'
                  )
            ) THEN 1 ELSE 0 END AS UploadIdUniqueIndexExists,
            CASE WHEN EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                  AND name = N'IX_Transfer_ArchivosLocales_ResultadoContexto'
                  AND is_disabled = 0
                  AND 4 = (
                      SELECT COUNT(*)
                      FROM sys.index_columns ic
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal > 0
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal = 1
                        AND c.name = N'ResultadoId'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal = 2
                        AND c.name = N'ColaboraId'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal = 3
                        AND c.name = N'MovimientoId'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.key_ordinal = 4
                        AND c.name = N'Estatus'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.is_included_column = 1
                        AND c.name = N'RFC_Origen'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id
                       AND c.column_id = ic.column_id
                      WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                        AND ic.index_id = sys.indexes.index_id
                        AND ic.is_included_column = 1
                        AND c.name = N'Ruta_ARchivo_Fisico'
                  )
            ) THEN 1 ELSE 0 END AS ResultContextIndexExists;
        """;

    public static async Task<BudiSchemaStatus> ReadAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = ReadStatusSql;
        command.CommandTimeout = 5;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new BudiSchemaStatus(false, false, false, false, false);

        return new BudiSchemaStatus(
            reader.GetInt32(0) == 1,
            reader.GetInt32(1) == 1,
            reader.GetInt32(2) == 1,
            reader.GetInt32(3) == 1,
            reader.GetInt32(4) == 1);
    }
}

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
/// Comprueba la conectividad y el contrato mínimo de esquema requerido en BuDi.
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

            var schema = await BudiSchemaRequirements.ReadAsync(connection, cancellationToken);
            return schema.IsReady
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Business store schema is incomplete.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Business store unavailable.", ex);
        }
    }
}
