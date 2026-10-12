using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WithdrawalReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceId",
                table: "Movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RequestedAmount = table.Column<int>(type: "integer", nullable: false),
                    ReservedAmount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAmount = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBusinessDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.CheckConstraint("CK_Quotes_Amounts", "\"RequestedAmount\" > 0 AND \"ReservedAmount\" > 0");
                    table.CheckConstraint("CK_Quotes_Confirmation", "(\"Status\" = 'CONFIRMED' AND \"ConfirmedAmount\" IS NOT NULL AND \"ConfirmedAmount\" > 0 AND \"ConfirmedAt\" IS NOT NULL AND \"ConfirmedBusinessDate\" IS NOT NULL) OR (\"Status\" <> 'CONFIRMED' AND \"ConfirmedAmount\" IS NULL AND \"ConfirmedAt\" IS NULL AND \"ConfirmedBusinessDate\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Quotes_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Confirmations",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Confirmations", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Confirmations_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteOptions_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteNoteAllocations",
                columns: table => new
                {
                    OptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteNoteAllocations", x => new { x.OptionId, x.CassetteId });
                    table.CheckConstraint("CK_QuoteNotes_Count", "\"Count\" BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_QuoteNoteAllocations_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteNoteAllocations_QuoteOptions_OptionId",
                        column: x => x.OptionId,
                        principalTable: "QuoteOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Confirmations_QuoteId",
                table: "Confirmations",
                column: "QuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteNoteAllocations_CassetteId",
                table: "QuoteNoteAllocations",
                column: "CassetteId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteOptions_QuoteId_Amount",
                table: "QuoteOptions",
                columns: new[] { "QuoteId", "Amount" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_AtmId",
                table: "Quotes",
                column: "AtmId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CardHash_Currency_Status_ConfirmedBusinessDate",
                table: "Quotes",
                columns: new[] { "CardHash", "Currency", "Status", "ConfirmedBusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Status_ExpiresAt_AtmId",
                table: "Quotes",
                columns: new[] { "Status", "ExpiresAt", "AtmId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Confirmations");

            migrationBuilder.DropTable(
                name: "QuoteNoteAllocations");

            migrationBuilder.DropTable(
                name: "QuoteOptions");

            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "Movements");
        }
    }
}
