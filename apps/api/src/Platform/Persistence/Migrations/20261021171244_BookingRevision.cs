using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BookingRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "service_requests",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_revision_positive",
                table: "service_requests",
                sql: "revision > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_revision_positive",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "service_requests");
        }
    }
}
