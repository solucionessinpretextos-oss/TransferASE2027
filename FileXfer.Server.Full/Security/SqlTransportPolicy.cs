using Microsoft.Data.SqlClient;

namespace FileXfer.Server.Security;

public static class SqlTransportPolicy
{
    public static void ValidateProduction(string name, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{name} es obligatoria.");

        SqlConnectionStringBuilder parsed;
        try
        {
            parsed = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} no es una cadena SQL Server válida.",
                ex);
        }

        var encryptionMode = parsed.Encrypt.ToString();
        if (string.Equals(encryptionMode, "False", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(encryptionMode, "Optional", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} debe exigir cifrado SQL (Encrypt=True o Strict)."
            );
        }

        if (parsed.TrustServerCertificate)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} no puede usar TrustServerCertificate=True en producción."
            );
        }

        if (parsed.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} no puede apuntar a LocalDB en producción."
            );
        }
    }
}
