using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations;

/// <summary>
/// Retira MFA del comportamiento activo sin destruir el esquema histórico.
/// Revoca sesiones previas para que todos obtengan tokens con la política e.firma vigente.
/// </summary>
[DbContext(typeof(FileXferDbContext))]
[Migration("20261019090000_DisableMfa2027")]
public partial class DisableMfa2027 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE [Users]
            SET [MfaRequired] = 0,
                [SecurityVersion] = [SecurityVersion] + 1,
                [LastModifiedUtc] = SYSUTCDATETIME(),
                [LastModifiedBy] = N'migration'
            WHERE [MfaRequired] = 1;

            UPDATE [MfaSecrets]
            SET [Active] = 0
            WHERE [Active] = 1;

            UPDATE [RefreshTokens]
            SET [IsRevoked] = 1,
                [MfaVerified] = 0
            WHERE [IsRevoked] = 0 OR [MfaVerified] = 1;

            INSERT INTO [AuditLogs]
                ([Id], [TimestampUtc], [ActorRfc], [Action], [Ip], [Details])
            VALUES
                (NEWID(), SYSUTCDATETIME(), N'migration', N'authentication_mfa_disabled', NULL,
                 N'MFA deshabilitado globalmente; enrolamientos desactivados y sesiones anteriores revocadas.');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No se reactiva MFA automáticamente: no existe una restauración segura de estados
        // y sesiones previos. Un rollback funcional debe restaurar el backup predespliegue.
    }
}
