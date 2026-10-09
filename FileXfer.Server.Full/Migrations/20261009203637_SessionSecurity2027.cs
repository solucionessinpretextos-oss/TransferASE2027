using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations
{
    /// <inheritdoc />
    public partial class SessionSecurity2027 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SecurityVersion",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AbsoluteExpiresUtc",
                table: "RefreshTokens",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "MfaVerified",
                table: "RefreshTokens",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentTokenId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityVersion",
                table: "RefreshTokens",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId_IsRevoked",
                table: "RefreshTokens",
                columns: new[] { "FamilyId", "IsRevoked" });

            // Los tokens emitidos antes de familias/versiones no pueden demostrar el nuevo
            // estado de seguridad. Se revocan durante la migración y se exige login completo.
            migrationBuilder.Sql(@"
                UPDATE [Users]
                SET [SecurityVersion] = 1
                WHERE [SecurityVersion] < 1;

                UPDATE [RefreshTokens]
                SET [IsRevoked] = 1,
                    [AbsoluteExpiresUtc] = [ExpiresUtc],
                    [FamilyId] = NEWID(),
                    [SecurityVersion] = 0,
                    [MfaVerified] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_FamilyId_IsRevoked",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AbsoluteExpiresUtc",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "MfaVerified",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "ParentTokenId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "RefreshTokens");
        }
    }
}
