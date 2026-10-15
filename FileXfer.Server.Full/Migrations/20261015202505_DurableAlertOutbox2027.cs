using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileXfer.Server.Migrations
{
    /// <inheritdoc />
    public partial class DurableAlertOutbox2027 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertOutboxDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Transport = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeduplicationBucket = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertOutboxDeliveries", x => x.Id);
                    table.CheckConstraint("CK_AlertOutboxDeliveries_AttemptCount", "[AttemptCount] >= 0");
                    table.CheckConstraint("CK_AlertOutboxDeliveries_DeliveredUtc", "[Status] <> N'Delivered' OR [DeliveredUtc] IS NOT NULL");
                    table.CheckConstraint("CK_AlertOutboxDeliveries_Lease", "([Status] = N'Processing' AND [LeaseId] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL) OR ([Status] <> N'Processing' AND [LeaseId] IS NULL AND [LeaseExpiresUtc] IS NULL)");
                    table.CheckConstraint("CK_AlertOutboxDeliveries_Status", "[Status] IN (N'Pending', N'Processing', N'Delivered', N'DeadLetter')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertOutboxDeliveries_EventId",
                table: "AlertOutboxDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertOutboxDeliveries_Status_NextAttemptUtc_LeaseExpiresUtc",
                table: "AlertOutboxDeliveries",
                columns: new[] { "Status", "NextAttemptUtc", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertOutboxDeliveries_Status_UpdatedUtc",
                table: "AlertOutboxDeliveries",
                columns: new[] { "Status", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertOutboxDeliveries_Transport_DeduplicationKey_DeduplicationBucket",
                table: "AlertOutboxDeliveries",
                columns: new[] { "Transport", "DeduplicationKey", "DeduplicationBucket" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertOutboxDeliveries");
        }
    }
}
