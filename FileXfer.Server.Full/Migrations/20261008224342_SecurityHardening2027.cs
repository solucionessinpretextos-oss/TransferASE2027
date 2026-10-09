using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations
{
    /// <inheritdoc />
    public partial class SecurityHardening2027 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM [Uploads]
                    WHERE LEN([Status]) > 32
                       OR LEN([Sha256Server]) > 64
                       OR LEN([Sha256Client]) > 64
                       OR LEN([Rfc]) > 13
                       OR LEN([OriginalFileName]) > 255
                       OR LEN([LastError]) > 1024
                       OR LEN([FileUuid]) > 32
                       OR LEN([DeviceId]) > 64)
                BEGIN
                    THROW 51001, 'SecurityHardening2027: existen filas de Uploads que exceden los nuevos límites. Corrija los datos y reintente; no se truncó información.', 1;
                END;

                IF EXISTS (
                    SELECT [Rfc], [DeviceId]
                    FROM [Devices]
                    GROUP BY [Rfc], [DeviceId]
                    HAVING COUNT_BIG(*) > 1)
                BEGIN
                    THROW 51002, 'SecurityHardening2027: existen dispositivos duplicados por RFC/DeviceId. Resuélvalos antes de crear el índice único.', 1;
                END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Uploads_Rfc_OriginalSourcePath",
                table: "Uploads");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Uploads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Server",
                table: "Uploads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Client",
                table: "Uploads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Rfc",
                table: "Uploads",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "OriginalFileName",
                table: "Uploads",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastError",
                table: "Uploads",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileUuid",
                table: "Uploads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                table: "Uploads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdActo",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdAuditoria",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdEnte",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdOficio",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdRequerimiento",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdResultado",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Periodo",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rfc = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nonces_ExpiresUtc",
                table: "Nonces",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Rfc_DeviceId",
                table: "Devices",
                columns: new[] { "Rfc", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_Nonces_ExpiresUtc",
                table: "Nonces");

            migrationBuilder.DropIndex(
                name: "IX_Devices_Rfc_DeviceId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IdActo",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "IdAuditoria",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "IdEnte",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "IdOficio",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "IdRequerimiento",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "IdResultado",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "Periodo",
                table: "Uploads");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Uploads",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Server",
                table: "Uploads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Client",
                table: "Uploads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Rfc",
                table: "Uploads",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(13)",
                oldMaxLength: 13);

            migrationBuilder.AlterColumn<string>(
                name: "OriginalFileName",
                table: "Uploads",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LastError",
                table: "Uploads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1024)",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileUuid",
                table: "Uploads",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                table: "Uploads",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Rfc_OriginalSourcePath",
                table: "Uploads",
                columns: new[] { "Rfc", "OriginalSourcePath" });
        }
    }
}
