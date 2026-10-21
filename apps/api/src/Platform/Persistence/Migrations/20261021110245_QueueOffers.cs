using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QueueOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_status",
                table: "service_request_items");

            migrationBuilder.CreateSequence(
                name: "queue_ticket_seq");

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "service_request_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "machine_id",
                table: "service_request_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "offered_at",
                table: "service_request_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "queue_ticket",
                table: "service_request_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "queued_at",
                table: "service_request_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "started_at",
                table: "service_request_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "active_item_id",
                table: "machines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_request_items_queue",
                table: "service_request_items",
                columns: new[] { "service_code", "status", "queue_ticket" });

            migrationBuilder.CreateIndex(
                name: "ux_service_request_items_active_machine",
                table: "service_request_items",
                column: "machine_id",
                unique: true,
                filter: "status IN ('to_be_confirmed', 'in_progress')");

            migrationBuilder.CreateIndex(
                name: "ux_service_request_items_queue_ticket",
                table: "service_request_items",
                column: "queue_ticket",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_assignment",
                table: "service_request_items",
                sql: "(status IN ('to_be_confirmed', 'in_progress')) = (machine_id IS NOT NULL)\nAND (status IN ('to_be_confirmed', 'in_progress')) = (offered_at IS NOT NULL)\nAND (status IN ('to_be_confirmed', 'in_progress')) = (expires_at IS NOT NULL)\nAND (offered_at IS NULL OR (expires_at > offered_at AND offered_at >= queued_at))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_queue_entry",
                table: "service_request_items",
                sql: "(status = 'pending_activation') = (queue_ticket IS NULL) AND (status = 'pending_activation') = (queued_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_started",
                table: "service_request_items",
                sql: "(status = 'in_progress') = (started_at IS NOT NULL) AND (started_at IS NULL OR (started_at >= offered_at AND started_at < expires_at))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_status",
                table: "service_request_items",
                sql: "status IN ('pending_activation', 'queued', 'to_be_confirmed', 'in_progress')");

            migrationBuilder.CreateIndex(
                name: "ux_machines_active_item_id",
                table: "machines",
                column: "active_item_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_machines_active_item",
                table: "machines",
                sql: "(state IN ('reserved', 'running') OR active_item_id IS NULL) AND (state <> 'reserved' OR active_item_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_machines_service_request_items",
                table: "machines",
                column: "active_item_id",
                principalTable: "service_request_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_request_items_machines",
                table: "service_request_items",
                column: "machine_id",
                principalTable: "machines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_machines_service_request_items",
                table: "machines");

            migrationBuilder.DropForeignKey(
                name: "fk_service_request_items_machines",
                table: "service_request_items");

            migrationBuilder.DropIndex(
                name: "ix_service_request_items_queue",
                table: "service_request_items");

            migrationBuilder.DropIndex(
                name: "ux_service_request_items_active_machine",
                table: "service_request_items");

            migrationBuilder.DropIndex(
                name: "ux_service_request_items_queue_ticket",
                table: "service_request_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_assignment",
                table: "service_request_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_queue_entry",
                table: "service_request_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_started",
                table: "service_request_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_status",
                table: "service_request_items");

            migrationBuilder.DropIndex(
                name: "ux_machines_active_item_id",
                table: "machines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_machines_active_item",
                table: "machines");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "machine_id",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "offered_at",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "queue_ticket",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "queued_at",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "service_request_items");

            migrationBuilder.DropColumn(
                name: "active_item_id",
                table: "machines");

            migrationBuilder.DropSequence(
                name: "queue_ticket_seq");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_status",
                table: "service_request_items",
                sql: "status IN ('pending_activation')");
        }
    }
}
