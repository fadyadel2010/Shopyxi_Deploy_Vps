using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113110000_AccountingPeriodAuthority")]
public sealed class AccountingPeriodAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("AccountingPeriodAuthorities", columns: t => new
        {
            TenantId = t.Column<Guid>(nullable: false), Revision = t.Column<long>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_AccountingPeriodAuthorities", x => x.TenantId);
            t.ForeignKey("FK_AccountingPeriodAuthorities_Tenants_TenantId", x => x.TenantId, "Tenants", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable("AccountingPeriods", columns: t => new
        {
            Id = t.Column<Guid>(nullable: false), TenantId = t.Column<Guid>(nullable: false),
            Code = t.Column<string>(maxLength: 50, nullable: false), Name = t.Column<string>(maxLength: 200, nullable: false),
            StartDate = t.Column<DateOnly>(type: "date", nullable: false), EndDate = t.Column<DateOnly>(type: "date", nullable: false),
            Status = t.Column<int>(nullable: false), Version = t.Column<long>(nullable: false),
            ClosedBy = t.Column<Guid>(nullable: true), ClosedAtUtc = t.Column<DateTime>(nullable: true),
            ReopenedBy = t.Column<Guid>(nullable: true), ReopenedAtUtc = t.Column<DateTime>(nullable: true),
            Reason = t.Column<string>(maxLength: 1000, nullable: false),
            CreatedAt = t.Column<DateTime>(nullable: false), UpdatedAt = t.Column<DateTime>(nullable: true),
            CreatedBy = t.Column<Guid>(nullable: true), UpdatedBy = t.Column<Guid>(nullable: true), IsDeleted = t.Column<bool>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_AccountingPeriods", x => x.Id);
            t.UniqueConstraint("AK_AccountingPeriods_TenantId_Id", x => new { x.TenantId, x.Id });
            t.ForeignKey("FK_AccountingPeriods_Tenants_TenantId", x => x.TenantId, "Tenants", "Id", onDelete: ReferentialAction.Restrict);
            t.CheckConstraint("CK_AccountingPeriods_Boundaries", "\"StartDate\" <= \"EndDate\"");
            t.CheckConstraint("CK_AccountingPeriods_Status", "\"Status\" IN (0,1,2)");
            t.CheckConstraint("CK_AccountingPeriods_Retained", "NOT \"IsDeleted\"");
        });
        migrationBuilder.CreateIndex("IX_AccountingPeriods_TenantId_Code", "AccountingPeriods", new[] { "TenantId", "Code" }, unique: true);
        migrationBuilder.CreateIndex("IX_AccountingPeriods_TenantId_StartDate_EndDate", "AccountingPeriods", new[] { "TenantId", "StartDate", "EndDate" });
        migrationBuilder.CreateTable("AccountingPeriodEvents", columns: t => new
        {
            Id = t.Column<Guid>(nullable: false), TenantId = t.Column<Guid>(nullable: false), AccountingPeriodId = t.Column<Guid>(nullable: false),
            PreviousStatus = t.Column<int>(nullable: true), Status = t.Column<int>(nullable: false), Version = t.Column<long>(nullable: false),
            Action = t.Column<string>(maxLength: 20, nullable: false), Reason = t.Column<string>(maxLength: 1000, nullable: false),
            TotalDebits = t.Column<decimal>(type: "numeric(18,4)", nullable: false), TotalCredits = t.Column<decimal>(type: "numeric(18,4)", nullable: false),
            JournalCount = t.Column<int>(nullable: false), CreatedAt = t.Column<DateTime>(nullable: false), UpdatedAt = t.Column<DateTime>(nullable: true),
            CreatedBy = t.Column<Guid>(nullable: true), UpdatedBy = t.Column<Guid>(nullable: true), IsDeleted = t.Column<bool>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_AccountingPeriodEvents", x => x.Id);
            t.ForeignKey("FK_PeriodEvents_Period", x => new { x.TenantId, x.AccountingPeriodId },
                "AccountingPeriods", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_AccountingPeriodEvents_TenantId_AccountingPeriodId_Version", "AccountingPeriodEvents", new[] { "TenantId", "AccountingPeriodId", "Version" }, unique: true);
        migrationBuilder.AddColumn<Guid>("AccountingPeriodId", "LedgerJournalEntries", nullable: true);
        migrationBuilder.CreateIndex("IX_LedgerJournalEntries_TenantId_AccountingPeriodId", "LedgerJournalEntries", new[] { "TenantId", "AccountingPeriodId" });
        migrationBuilder.AddForeignKey("FK_LedgerJournal_Period", "LedgerJournalEntries",
            new[] { "TenantId", "AccountingPeriodId" }, "AccountingPeriods", principalColumns: new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
        // No tenant periods, business transactions, or historical journal updates are seeded.
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Accounting period history is retained. Roll back application images only; do not down-migrate financial history.");
}
