using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace FileXfer.Server.Data;

/// <summary>
/// Usa la misma configuración de desarrollo que el servidor para que las operaciones
/// de Entity Framework nunca terminen accidentalmente en LocalDB u otra base distinta.
/// No se utiliza en tiempo de ejecución.
/// </summary>
public sealed class FileXferDbContextFactory : IDesignTimeDbContextFactory<FileXferDbContext>
{
    public FileXferDbContext CreateDbContext(string[] args)
    {
        var configurationRoot = ResolveConfigurationRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configurationRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("FileXferDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:FileXferDb no está configurada para las operaciones de diseño.");
        }

        var options = new DbContextOptionsBuilder<FileXferDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new FileXferDbContext(options);
    }

    private static string ResolveConfigurationRoot()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentDirectory, "appsettings.Development.json")))
            return currentDirectory;

        var projectDirectory = Path.Combine(currentDirectory, "FileXfer.Server.Full");
        if (File.Exists(Path.Combine(projectDirectory, "appsettings.Development.json")))
            return projectDirectory;

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json")))
            return AppContext.BaseDirectory;

        throw new DirectoryNotFoundException(
            "No se encontró appsettings.Development.json para configurar FileXferDb.");
    }
}
