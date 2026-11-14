using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>Restore the sequence already required by the transfer API; never renumber history.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113100000_StockTransferNumberSequenceAuthority")]
public sealed class StockTransferNumberSequenceAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        migrationBuilder.Sql("""
            DO $$
            DECLARE
                max_transfer_number bigint;
                current_last_value bigint;
                current_is_called boolean;
                current_next_value bigint;
            BEGIN
                CREATE SEQUENCE IF NOT EXISTS public.stock_transfer_number_seq
                    START WITH 1000 INCREMENT BY 1 MINVALUE 1000 CACHE 1;

                IF to_regclass('public."StockTransfers"') IS NULL THEN RETURN; END IF;

                SELECT COALESCE(MAX((regexp_match("TransferNumber", '^TRF-\d{8}-(\d+)$'))[1]::bigint), 0)
                INTO max_transfer_number FROM public."StockTransfers"
                WHERE "TransferNumber" ~ '^TRF-\d{8}-\d+$';

                SELECT last_value, is_called INTO current_last_value, current_is_called
                FROM public.stock_transfer_number_seq;
                current_next_value := CASE WHEN current_is_called THEN current_last_value + 1 ELSE current_last_value END;
                IF max_transfer_number >= 1000 AND current_next_value <= max_transfer_number THEN
                    PERFORM setval('public.stock_transfer_number_seq', max_transfer_number, true);
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Durable identifiers must not be reused on downgrade/re-upgrade.
        // Preserve the allocator and every existing transfer number.
    }
}
