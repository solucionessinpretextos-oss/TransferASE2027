using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations
{
    /// <inheritdoc />
    public partial class TransferCapabilities2027 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ExpectedLength",
                table: "Uploads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedSha256",
                table: "Uploads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActivityUtc",
                table: "Uploads",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "SYSDATETIMEOFFSET()");

            migrationBuilder.AddColumn<string>(
                name: "PolicyExtension",
                table: "Uploads",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyVersion",
                table: "Uploads",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceiptId",
                table: "Uploads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResumeExpiresUtc",
                table: "Uploads",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResumedBytes",
                table: "Uploads",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "Uploads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationOutcome",
                table: "Uploads",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationPerformed",
                table: "Uploads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationRequired",
                table: "Uploads",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "FileTypePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsAllowed = table.Column<bool>(type: "bit", nullable: false),
                    RequiresVerification = table.Column<bool>(type: "bit", nullable: false),
                    MaxFileBytes = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTypePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptChainStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    HeadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptChainStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UploadReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CanonicalPayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreviousChainHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChainHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SignatureBase64 = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SignatureAlgorithm = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicKeyPem = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadReceipts_Uploads_UploadId",
                        column: x => x.UploadId,
                        principalTable: "Uploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FileTypePolicyRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsAllowed = table.Column<bool>(type: "bit", nullable: false),
                    RequiresVerification = table.Column<bool>(type: "bit", nullable: false),
                    MaxFileBytes = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ChangedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTypePolicyRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileTypePolicyRevisions_FileTypePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "FileTypePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_ReceiptId",
                table: "Uploads",
                column: "ReceiptId",
                unique: true,
                filter: "[ReceiptId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Status_ResumeExpiresUtc",
                table: "Uploads",
                columns: new[] { "Status", "ResumeExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FileTypePolicies_Extension",
                table: "FileTypePolicies",
                column: "Extension",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileTypePolicyRevisions_Extension_ChangedUtc",
                table: "FileTypePolicyRevisions",
                columns: new[] { "Extension", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FileTypePolicyRevisions_PolicyId_Version",
                table: "FileTypePolicyRevisions",
                columns: new[] { "PolicyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadReceipts_IssuedUtc",
                table: "UploadReceipts",
                column: "IssuedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UploadReceipts_UploadId",
                table: "UploadReceipts",
                column: "UploadId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Uploads_UploadReceipts_ReceiptId",
                table: "Uploads",
                column: "ReceiptId",
                principalTable: "UploadReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Uploads_UploadReceipts_ReceiptId",
                table: "Uploads");

            migrationBuilder.DropTable(
                name: "FileTypePolicyRevisions");

            migrationBuilder.DropTable(
                name: "ReceiptChainStates");

            migrationBuilder.DropTable(
                name: "UploadReceipts");

            migrationBuilder.DropTable(
                name: "FileTypePolicies");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_ReceiptId",
                table: "Uploads");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_Status_ResumeExpiresUtc",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ExpectedLength",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ExpectedSha256",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "LastActivityUtc",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "PolicyExtension",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ReceiptId",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ResumeExpiresUtc",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ResumedBytes",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "VerificationOutcome",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "VerificationPerformed",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "VerificationRequired",
                table: "Uploads");
        }
    }
}
