using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AtmCash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationsAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LowCashAlertActive",
                table: "Cassettes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingReconciliationId",
                table: "Atms",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TryRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Currency);
                    table.CheckConstraint("CK_ExchangeRates_Value", "\"TryRate\" > 0 AND (\"Currency\" <> 'TRY' OR \"TryRate\" = 1)");
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteNumber = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    AvailableCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Outbox_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reconciliations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalDifferenceTry = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reconciliations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reconciliations_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconciliationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteNumber = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    SystemCount = table.Column<int>(type: "integer", nullable: false),
                    CountedCount = table.Column<int>(type: "integer", nullable: false),
                    TryRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationLines", x => x.Id);
                    table.CheckConstraint("CK_Reconciliation_Counts", "\"SystemCount\" BETWEEN 0 AND 100 AND \"CountedCount\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_ReconciliationLines_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationLines_Reconciliations_ReconciliationId",
                        column: x => x.ReconciliationId,
                        principalTable: "Reconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ExchangeRates",
                columns: new[] { "Currency", "TryRate" },
                values: new object[,]
                {
                    { "EUR", 40m },
                    { "TRY", 1m },
                    { "USD", 35m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Atms_PendingReconciliationId",
                table: "Atms",
                column: "PendingReconciliationId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_CassetteId",
                table: "Outbox",
                column: "CassetteId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_SentAt_CreatedAt",
                table: "Outbox",
                columns: new[] { "SentAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationLines_CassetteId",
                table: "ReconciliationLines",
                column: "CassetteId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationLines_ReconciliationId_CassetteId_Currency_De~",
                table: "ReconciliationLines",
                columns: new[] { "ReconciliationId", "CassetteId", "Currency", "Denomination" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reconciliations_AtmId",
                table: "Reconciliations",
                column: "AtmId");

            migrationBuilder.AddForeignKey(
                name: "FK_Atms_Reconciliations_PendingReconciliationId",
                table: "Atms",
                column: "PendingReconciliationId",
                principalTable: "Reconciliations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Atms_Reconciliations_PendingReconciliationId",
                table: "Atms");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "ReconciliationLines");

            migrationBuilder.DropTable(
                name: "Reconciliations");

            migrationBuilder.DropIndex(
                name: "IX_Atms_PendingReconciliationId",
                table: "Atms");

            migrationBuilder.DropColumn(
                name: "LowCashAlertActive",
                table: "Cassettes");

            migrationBuilder.DropColumn(
                name: "PendingReconciliationId",
                table: "Atms");
        }
    }
}
