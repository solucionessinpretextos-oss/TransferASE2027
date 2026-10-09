// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Migrations/20251105180306_Initial.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Migración generada por Entity Framework para la base interna FileXferDb.
// Interacción: EF Core la usa para crear/actualizar estructura de tablas internas del servidor.
// Regla de mantenimiento: No editar manualmente salvo corrección controlada; preferir generar una nueva migración.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations
{
    public partial class Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorRfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    PublicKeyPem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FriendlyName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Revoked = table.Column<bool>(type: "bit", nullable: false),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lockouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LockoutUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lockouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MfaSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    SecretBase32 = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaSecrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nonces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValueBase64 = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nonces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RfcProvisioningRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfcProvisioningRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Uploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    FileUuid = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    OriginalSourcePath = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    StoredRelativePath = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BytesReceived = table.Column<long>(type: "bigint", nullable: false),
                    Sha256Server = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sha256Client = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uploads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MfaRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Lockouts_Rfc",
                table: "Lockouts",
                column: "Rfc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MfaSecrets_Rfc_Active",
                table: "MfaSecrets",
                columns: new[] { "Rfc", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_Nonces_ValueBase64",
                table: "Nonces",
                column: "ValueBase64");

            migrationBuilder.CreateIndex(
                name: "IX_RfcProvisioningRequests_Rfc",
                table: "RfcProvisioningRequests",
                column: "Rfc");

            migrationBuilder.CreateIndex(
                name: "IX_RfcProvisioningRequests_Status_CreatedUtc",
                table: "RfcProvisioningRequests",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_FileUuid",
                table: "Uploads",
                column: "FileUuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Rfc_OriginalFileName",
                table: "Uploads",
                columns: new[] { "Rfc", "OriginalFileName" });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Rfc_OriginalSourcePath",
                table: "Uploads",
                columns: new[] { "Rfc", "OriginalSourcePath" });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_StoredRelativePath",
                table: "Uploads",
                column: "StoredRelativePath");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Rfc",
                table: "Users",
                column: "Rfc",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Lockouts");

            migrationBuilder.DropTable(
                name: "MfaSecrets");

            migrationBuilder.DropTable(
                name: "Nonces");

            migrationBuilder.DropTable(
                name: "RfcProvisioningRequests");

            migrationBuilder.DropTable(
                name: "Uploads");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
