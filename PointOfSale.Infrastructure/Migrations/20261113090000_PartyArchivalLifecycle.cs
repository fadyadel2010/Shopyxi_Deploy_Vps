using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>
/// Adds an auditable active/archive lifecycle to POS customers and suppliers.
/// Historical documents remain linked; existing rows remain active.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113090000_PartyArchivalLifecycle")]
public sealed class PartyArchivalLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            table: "Customers",
            type: "boolean",
            nullable: false,
            defaultValue: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "LastArchivedAtUtc",
            table: "Customers",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "LastArchivedByUserId",
            table: "Customers",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "LastReactivatedAtUtc",
            table: "Customers",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "LastReactivatedByUserId",
            table: "Customers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            table: "Suppliers",
            type: "boolean",
            nullable: false,
            defaultValue: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "LastArchivedAtUtc",
            table: "Suppliers",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "LastArchivedByUserId",
            table: "Suppliers",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "LastReactivatedAtUtc",
            table: "Suppliers",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "LastReactivatedByUserId",
            table: "Suppliers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Customers_LastArchivedActorTime",
            table: "Customers",
            sql: "(\"LastArchivedAtUtc\" IS NULL) = (\"LastArchivedByUserId\" IS NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "CK_Customers_LastReactivatedActorTime",
            table: "Customers",
            sql: "(\"LastReactivatedAtUtc\" IS NULL) = (\"LastReactivatedByUserId\" IS NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "CK_Suppliers_LastArchivedActorTime",
            table: "Suppliers",
            sql: "(\"LastArchivedAtUtc\" IS NULL) = (\"LastArchivedByUserId\" IS NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "CK_Suppliers_LastReactivatedActorTime",
            table: "Suppliers",
            sql: "(\"LastReactivatedAtUtc\" IS NULL) = (\"LastReactivatedByUserId\" IS NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_Customers_TenantId_IsActive_Name",
            table: "Customers",
            columns: new[] { "TenantId", "IsActive", "Name" });
        migrationBuilder.CreateIndex(
            name: "IX_Suppliers_TenantId_IsActive_Name",
            table: "Suppliers",
            columns: new[] { "TenantId", "IsActive", "Name" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Customers_TenantId_IsActive_Name", table: "Customers");
        migrationBuilder.DropIndex(name: "IX_Suppliers_TenantId_IsActive_Name", table: "Suppliers");
        migrationBuilder.DropCheckConstraint(name: "CK_Customers_LastArchivedActorTime", table: "Customers");
        migrationBuilder.DropCheckConstraint(name: "CK_Customers_LastReactivatedActorTime", table: "Customers");
        migrationBuilder.DropCheckConstraint(name: "CK_Suppliers_LastArchivedActorTime", table: "Suppliers");
        migrationBuilder.DropCheckConstraint(name: "CK_Suppliers_LastReactivatedActorTime", table: "Suppliers");
        migrationBuilder.DropColumn(name: "IsActive", table: "Customers");
        migrationBuilder.DropColumn(name: "LastArchivedAtUtc", table: "Customers");
        migrationBuilder.DropColumn(name: "LastArchivedByUserId", table: "Customers");
        migrationBuilder.DropColumn(name: "LastReactivatedAtUtc", table: "Customers");
        migrationBuilder.DropColumn(name: "LastReactivatedByUserId", table: "Customers");
        migrationBuilder.DropColumn(name: "IsActive", table: "Suppliers");
        migrationBuilder.DropColumn(name: "LastArchivedAtUtc", table: "Suppliers");
        migrationBuilder.DropColumn(name: "LastArchivedByUserId", table: "Suppliers");
        migrationBuilder.DropColumn(name: "LastReactivatedAtUtc", table: "Suppliers");
        migrationBuilder.DropColumn(name: "LastReactivatedByUserId", table: "Suppliers");
    }
}
