using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MachineIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "machine_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_incidents", x => x.id);
                    table.CheckConstraint("ck_machine_incidents_description", "description IS NULL OR char_length(description) BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_machine_incidents_status", "status IN ('reported', 'confirmed')");
                    table.ForeignKey(
                        name: "fk_machine_incidents_machines",
                        column: x => x.machine_id,
                        principalTable: "machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_incidents_service_request_items",
                        column: x => x.request_item_id,
                        principalTable: "service_request_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_machine_incidents_machine_id",
                table: "machine_incidents",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "ux_machine_incidents_item_reported",
                table: "machine_incidents",
                column: "request_item_id",
                unique: true,
                filter: "status = 'reported'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machine_incidents");
        }
    }
}
