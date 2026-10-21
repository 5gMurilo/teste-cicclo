using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "machines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    machine_type = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machines", x => x.id);
                    table.CheckConstraint("ck_machines_machine_type", "machine_type IN ('washer', 'dryer')");
                    table.CheckConstraint("ck_machines_state", "state IN ('available', 'reserved', 'running', 'out_of_service')");
                });

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    machine_type = table.Column<string>(type: "text", nullable: false),
                    current_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_services", x => x.code);
                    table.CheckConstraint("ck_services_code_machine_type", "(code = 'wash' AND machine_type = 'washer') OR (code = 'dry' AND machine_type = 'dryer')");
                    table.CheckConstraint("ck_services_current_price_positive", "current_price > 0");
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.id);
                    table.CheckConstraint("ck_wallets_balance_non_negative", "balance >= 0");
                    table.CheckConstraint("ck_wallets_currency_brl", "currency = 'BRL'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_machines_machine_type_state",
                table: "machines",
                columns: new[] { "machine_type", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_machines_name",
                table: "machines",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machines");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropTable(
                name: "wallets");
        }
    }
}
