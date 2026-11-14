using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>
/// Adds durable registration replay and application provisioning authority.
/// Existing tenant lifecycle and StoreConfig publication state are unchanged.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261111160000_AutomatedTenantProvisioning")]
public sealed class AutomatedTenantProvisioning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new NotSupportedException(
                "Automated tenant provisioning is production-supported only on PostgreSQL.");
        }

        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "Tenants"
                    WHERE "Slug" IS NOT NULL
                    GROUP BY LOWER("Slug")
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23505',
                        MESSAGE = 'TENANT_SLUG_CASE_INSENSITIVE_DUPLICATES',
                        HINT = 'Resolve duplicate Tenant.Slug values that differ only by case before retrying this migration.';
                END IF;
            END
            $$;

            CREATE UNIQUE INDEX "UX_Tenants_Slug_CaseInsensitive"
            ON "Tenants" (LOWER("Slug"))
            WHERE "Slug" IS NOT NULL;
            """);

        migrationBuilder.CreateTable(
            name: "TenantProvisioningOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                StoreConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CanonicalHostname = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastErrorCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LastErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                AccountsSeededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                StorefrontProjectionQueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                VerificationDispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantProvisioningOperations", operation => operation.Id);
                table.CheckConstraint(
                    "CK_TenantProvisioningOperations_AttemptCount",
                    "\"AttemptCount\" >= 0");
                table.CheckConstraint(
                    "CK_TenantProvisioningOperations_Hostname",
                    "\"CanonicalHostname\" = LOWER(\"CanonicalHostname\") AND \"CanonicalHostname\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?\\.shopyxi\\.com$'");
                table.ForeignKey(
                    name: "FK_TenantProvisioningOperations_StoreConfigs_StoreConfigId",
                    column: operation => operation.StoreConfigId,
                    principalTable: "StoreConfigs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TenantProvisioningOperations_Tenants_TenantId",
                    column: operation => operation.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TenantProvisioningOperations_Users_OwnerUserId",
                    column: operation => operation.OwnerUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TenantProvisioningOperations_LeaseExpiry",
            table: "TenantProvisioningOperations",
            column: "LeaseExpiresAtUtc");
        migrationBuilder.CreateIndex(
            name: "IX_TenantProvisioningOperations_OwnerUserId",
            table: "TenantProvisioningOperations",
            column: "OwnerUserId");
        migrationBuilder.CreateIndex(
            name: "IX_TenantProvisioningOperations_Recovery",
            table: "TenantProvisioningOperations",
            columns: new[] { "State", "NextAttemptAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_TenantProvisioningOperations_StoreConfigId",
            table: "TenantProvisioningOperations",
            column: "StoreConfigId");
        migrationBuilder.CreateIndex(
            name: "UQ_TenantProvisioningOperations_IdempotencyKeyHash",
            table: "TenantProvisioningOperations",
            column: "IdempotencyKeyHash",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "UQ_TenantProvisioningOperations_TenantId",
            table: "TenantProvisioningOperations",
            column: "TenantId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TenantProvisioningOperations");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"UX_Tenants_Slug_CaseInsensitive\";");
    }
}
