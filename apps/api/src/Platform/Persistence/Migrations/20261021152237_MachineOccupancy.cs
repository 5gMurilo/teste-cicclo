using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MachineOccupancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_demo_occupied",
                table: "machines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Antes desta coluna, running sem item já era a única forma de ocupação sintética (ck_machines_active_item);
            // a marca passa a ser explícita sem alterar estado nem vínculo de nenhuma máquina.
            migrationBuilder.Sql("UPDATE machines SET is_demo_occupied = (state = 'running' AND active_item_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_machines_demo_occupied",
                table: "machines",
                sql: "is_demo_occupied = (state = 'running' AND active_item_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_machines_demo_occupied",
                table: "machines");

            migrationBuilder.DropColumn(
                name: "is_demo_occupied",
                table: "machines");
        }
    }
}
