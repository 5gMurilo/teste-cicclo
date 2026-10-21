using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicclo.Api.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BookingLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_requests", x => x.id);
                    table.CheckConstraint("ck_service_requests_request_fingerprint_sha256", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_service_requests_total_amount_positive", "total_amount > 0");
                    table.ForeignKey(
                        name: "fk_service_requests_wallets",
                        column: x => x.wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_request_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_code = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_request_items", x => x.id);
                    table.CheckConstraint("ck_service_request_items_status", "status IN ('pending_activation')");
                    table.CheckConstraint("ck_service_request_items_unit_price_positive", "unit_price > 0");
                    table.ForeignKey(
                        name: "fk_service_request_items_service_requests",
                        column: x => x.request_id,
                        principalTable: "service_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_items_services",
                        column: x => x.service_code,
                        principalTable: "services",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_transactions", x => x.id);
                    table.CheckConstraint("ck_wallet_transactions_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_wallet_transactions_balance_after_non_negative", "balance_after >= 0");
                    table.CheckConstraint("ck_wallet_transactions_debit_without_item", "kind <> 'debit' OR request_item_id IS NULL");
                    table.CheckConstraint("ck_wallet_transactions_kind", "kind IN ('debit')");
                    table.ForeignKey(
                        name: "fk_wallet_transactions_service_request_items",
                        column: x => x.request_item_id,
                        principalTable: "service_request_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_transactions_service_requests",
                        column: x => x.request_id,
                        principalTable: "service_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_transactions_wallets",
                        column: x => x.wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_request_items_request_id",
                table: "service_request_items",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_request_items_service_code",
                table: "service_request_items",
                column: "service_code");

            migrationBuilder.CreateIndex(
                name: "ux_service_requests_wallet_idempotency_key",
                table: "service_requests",
                columns: new[] { "wallet_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_request_item_id",
                table: "wallet_transactions",
                column: "request_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_wallet_id",
                table: "wallet_transactions",
                column: "wallet_id");

            migrationBuilder.CreateIndex(
                name: "ux_wallet_transactions_request_debit",
                table: "wallet_transactions",
                column: "request_id",
                unique: true,
                filter: "kind = 'debit'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_transactions");

            migrationBuilder.DropTable(
                name: "service_request_items");

            migrationBuilder.DropTable(
                name: "service_requests");
        }
    }
}
