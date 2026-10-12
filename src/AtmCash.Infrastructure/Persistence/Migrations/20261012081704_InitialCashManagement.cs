using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCashManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Atms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HasCashLoaded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Atms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cassettes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    Denomination = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cassettes", x => x.Id);
                    table.CheckConstraint("CK_Cassettes_Configuration", "(\"Currency\" IS NULL AND \"Denomination\" IS NULL) OR (\"Currency\" IS NOT NULL AND \"Denomination\" IS NOT NULL AND \"Currency\" IN ('TRY', 'USD', 'EUR') AND \"Denomination\" IN (20, 50, 100, 500))");
                    table.CheckConstraint("CK_Cassettes_Number", "\"Number\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Cassettes_TypeSlot", "(\"Number\" = 5 AND \"Type\" = 'DepositOnly' AND \"Currency\" IS NULL AND \"Denomination\" IS NULL) OR (\"Number\" < 5 AND \"Type\" = 'Recycling')");
                    table.ForeignKey(
                        name: "FK_Cassettes_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Inventory",
                columns: table => new
                {
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    NoteCount = table.Column<int>(type: "integer", nullable: false),
                    ReservedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventory", x => new { x.CassetteId, x.Currency, x.Denomination });
                    table.CheckConstraint("CK_Inventory_Counts", "\"NoteCount\" BETWEEN 0 AND 100 AND \"ReservedCount\" BETWEEN 0 AND \"NoteCount\"");
                    table.CheckConstraint("CK_Inventory_Notes", "\"Currency\" IN ('TRY', 'USD', 'EUR') AND \"Denomination\" IN (20, 50, 100, 500)");
                    table.ForeignKey(
                        name: "FK_Inventory_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    NoteDelta = table.Column<int>(type: "integer", nullable: false),
                    ReservedDelta = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Movements_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cassettes_AtmId_Number",
                table: "Cassettes",
                columns: new[] { "AtmId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CassetteId_OccurredAt",
                table: "Movements",
                columns: new[] { "CassetteId", "OccurredAt" });

            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_movement_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Movement records are immutable';
                END;
                $$;
                CREATE TRIGGER movements_immutable
                    BEFORE UPDATE OR DELETE ON "Movements"
                    FOR EACH ROW EXECUTE FUNCTION prevent_movement_mutation();
                CREATE TRIGGER movements_no_truncate
                    BEFORE TRUNCATE ON "Movements"
                    FOR EACH STATEMENT EXECUTE FUNCTION prevent_movement_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Inventory");

            migrationBuilder.DropTable(
                name: "Movements");

            migrationBuilder.Sql("DROP FUNCTION prevent_movement_mutation();");

            migrationBuilder.DropTable(
                name: "Cassettes");

            migrationBuilder.DropTable(
                name: "Atms");
        }
    }
}
