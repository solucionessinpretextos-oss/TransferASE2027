// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Data/FileXferDbContext.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Modelo de persistencia interna FileXferDb: representa entidades propias del servidor, no necesariamente tablas BuDi.
// Interacción: Entity Framework usa estas clases para autenticación, auditoría, nonces, refresh tokens y registro interno de cargas.
// Regla de mantenimiento: Cambios requieren migración EF y revisión de datos existentes.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using System.Linq;

namespace FileXfer.Server.Data
{
    public class FileXferDbContext : DbContext
    {
        public FileXferDbContext(DbContextOptions<FileXferDbContext> options) : base(options) { }

        public DbSet<RfcUser> Users => Set<RfcUser>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<MfaSecret> MfaSecrets => Set<MfaSecret>();
        public DbSet<Nonce> Nonces => Set<Nonce>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<Lockout> Lockouts => Set<Lockout>();
        public DbSet<UploadEntry> Uploads => Set<UploadEntry>();
        public DbSet<RfcProvisioningRequest> RfcProvisioningRequests => Set<RfcProvisioningRequest>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var rolesConverter = new ValueConverter<string[], string>(
                v => string.Join('|', v ?? Array.Empty<string>()),
                v => string.IsNullOrEmpty(v)
                        ? Array.Empty<string>()
                        : v.Split('|', StringSplitOptions.RemoveEmptyEntries)
            );

            var rolesComparer = new ValueComparer<string[]>(
                (a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
                v => (v ?? Array.Empty<string>())
                        .Aggregate(17, (acc, s) => acc * 23 + (s == null ? 0 : s.GetHashCode())),
                v => v == null ? Array.Empty<string>() : v.ToArray()
            );

            modelBuilder.Entity<RfcUser>()
                .HasIndex(x => x.Rfc)
                .IsUnique();

            modelBuilder.Entity<RfcUser>()
                .Property(x => x.Roles)
                .HasConversion(rolesConverter)
                .Metadata.SetValueComparer(rolesComparer);

            modelBuilder.Entity<MfaSecret>()
                .HasIndex(x => new { x.Rfc, x.Active });

            modelBuilder.Entity<Device>()
                .HasIndex(x => new { x.Rfc, x.DeviceId })
                .IsUnique();

            modelBuilder.Entity<AuditLog>()
                .HasIndex(x => x.TimestampUtc);

            modelBuilder.Entity<Lockout>()
                .HasIndex(x => x.Rfc)
                .IsUnique();

            modelBuilder.Entity<Nonce>()
                .HasIndex(x => x.ValueBase64);

            modelBuilder.Entity<Nonce>()
                .HasIndex(x => x.ExpiresUtc);

            modelBuilder.Entity<UploadEntry>()
                .HasIndex(x => new { x.Rfc, x.OriginalFileName });

            modelBuilder.Entity<UploadEntry>()
                .HasIndex(x => x.FileUuid)
                .IsUnique();

            modelBuilder.Entity<UploadEntry>()
                .HasIndex(x => x.StoredRelativePath);

            modelBuilder.Entity<RfcProvisioningRequest>()
                .HasIndex(x => x.Rfc);

            modelBuilder.Entity<RfcProvisioningRequest>()
                .HasIndex(x => new { x.Status, x.CreatedUtc });
            modelBuilder.Entity<RefreshToken>()
                .HasIndex(x => x.TokenHash)
                .IsUnique();
            modelBuilder.Entity<RefreshToken>()
                .HasIndex(x => new { x.FamilyId, x.IsRevoked });
        }
    }
}
