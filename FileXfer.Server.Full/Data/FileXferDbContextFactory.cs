using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileXfer.Server.Data;

/// <summary>
/// Mantiene el diseño de migraciones independiente del arranque del servicio y evita
/// contactar una base real durante el scaffolding. No se utiliza en tiempo de ejecución.
/// </summary>
public sealed class FileXferDbContextFactory : IDesignTimeDbContextFactory<FileXferDbContext>
{
    public FileXferDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FileXferDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=FileXferDesign;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new FileXferDbContext(options);
    }
}
