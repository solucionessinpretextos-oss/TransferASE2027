using Microsoft.Data.SqlClient;
using System.Net;

namespace FileXfer.Server.Security;

public static class SqlTransportPolicy
{
    public static void ValidateProduction(string name, string? connectionString)
    {
        var parsed = Parse(name, connectionString);

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

    public static void ValidateRestrictedDevelopmentDatabase(
        string name,
        string? connectionString,
        string expectedDatabase)
    {
        var parsed = Parse(name, connectionString);
        if (!string.Equals(parsed.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} solo puede omitir TLS para la base de pruebas {expectedDatabase}.");
        }

        if (parsed.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase) ||
            !IsPrivateIpv4DataSource(parsed.DataSource))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} solo puede omitir TLS hacia una IPv4 privada explícita.");
        }
    }

    private static SqlConnectionStringBuilder Parse(string name, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{name} es obligatoria.");

        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} no es una cadena SQL Server válida.",
                ex);
        }
    }

    private static bool IsPrivateIpv4DataSource(string dataSource)
    {
        var host = dataSource.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        var separator = host.IndexOfAny(['\\', ',']);
        if (separator >= 0)
            host = host[..separator];

        if (!IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
