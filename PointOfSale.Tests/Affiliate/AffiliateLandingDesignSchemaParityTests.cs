using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateLandingDesignSchemaParityTests
{
    [Fact]
    public void SeparateAdditiveScript_ContainsBothModelForeignKeyLookupIndexes()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var script = File.ReadAllText(Path.Combine(root, "DB/migrations/20261025_AffiliateLandingPageDesigns.sql"));
        var snapshot = File.ReadAllText(Path.Combine(root, "PointOfSale.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs"));
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"IX_AffiliateLandingPageDesigns_ProductId\"", script);
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"IX_AffiliateLandingPageDesigns_TenantId\"", script);
        Assert.Contains("b.HasIndex(\"ProductId\")", snapshot);
        Assert.Contains("b.HasIndex(\"TenantId\")", snapshot);
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
    }
}
