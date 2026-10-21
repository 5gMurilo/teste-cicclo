using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CancellationRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_wallet_transactions_kind",
                table: "wallet_transactions");

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

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at",
                table: "service_request_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_wallet_transactions_item_refund",
                table: "wallet_transactions",
                column: "request_item_id",
                unique: true,
                filter: "kind = 'refund'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_wallet_transactions_kind",
                table: "wallet_transactions",
                sql: "kind IN ('debit', 'refund')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_wallet_transactions_refund_with_item",
                table: "wallet_transactions",
                sql: "kind <> 'refund' OR request_item_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_assignment",
                table: "service_request_items",
                sql: "(status IN ('to_be_confirmed', 'in_progress', 'completed', 'failed')) = (machine_id IS NOT NULL)\nAND (status IN ('to_be_confirmed', 'in_progress', 'completed', 'failed')) = (offered_at IS NOT NULL)\nAND (status IN ('to_be_confirmed', 'in_progress', 'completed', 'failed')) = (expires_at IS NOT NULL)\nAND (offered_at IS NULL OR (expires_at > offered_at AND offered_at >= queued_at))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_cancelled",
                table: "service_request_items",
                sql: "(status = 'cancelled') = (cancelled_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_queue_entry",
                table: "service_request_items",
                sql: "CASE status\n    WHEN 'pending_activation' THEN queue_ticket IS NULL AND queued_at IS NULL\n    WHEN 'cancelled' THEN (queue_ticket IS NULL) = (queued_at IS NULL)\n    ELSE queue_ticket IS NOT NULL AND queued_at IS NOT NULL\nEND");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_started",
                table: "service_request_items",
                sql: "(status IN ('in_progress', 'completed', 'failed')) = (started_at IS NOT NULL) AND (started_at IS NULL OR (started_at >= offered_at AND started_at < expires_at))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_request_items_status",
                table: "service_request_items",
                sql: "status IN ('pending_activation', 'queued', 'to_be_confirmed', 'in_progress', 'cancelled', 'completed', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_wallet_transactions_item_refund",
                table: "wallet_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_wallet_transactions_kind",
                table: "wallet_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_wallet_transactions_refund_with_item",
                table: "wallet_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_assignment",
                table: "service_request_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_request_items_cancelled",
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

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "service_request_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_wallet_transactions_kind",
                table: "wallet_transactions",
                sql: "kind IN ('debit')");

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
        }
    }
}
