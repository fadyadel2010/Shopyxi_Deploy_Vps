using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

public sealed class GeographicTenantMutationBoundaryTests
{
    [Fact]
    public void EveryTenantMetadataWriterUsesTheHeldCurrentAuthorityBoundary()
    {
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")
            ?? throw new InvalidOperationException("Explicit current source root required.");
        var methods = Directory.GetFiles(Path.Combine(root, "PointOfSale.API", "Services", "Admin"), "GeographicAdministrationService*.cs")
            .SelectMany(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(m => m.Parent is ClassDeclarationSyntax { Identifier.ValueText: "GeographicAdministrationService" })
            .ToArray();
        string[] tenantWriters = ["CreateMappingDraftAsync", "AddMappingAsync", "RecordSyncRunAsync", "AddSyncCandidateAsync",
            "ReviewSyncCandidateAsync", "CreateOverrideAsync", "ApproveOverrideAsync", "PublishOrRollbackMappingReleaseAsync",
            "ValidateMappingDraftAsync", "AcceptBostaMappingAsync", "SynchronizeBostaReferenceAsync"];
        foreach (var name in tenantWriters)
        {
            var method = Assert.Single(methods, m => m.Identifier.ValueText == name);
            var owner = Assert.Single(method.Body!.Statements.OfType<LocalDeclarationStatementSyntax>(), d =>
                d.Declaration.Variables.Any(v => v.Initializer?.Value.ToString().StartsWith("await BeginTenantGeographicTransactionAsync(", StringComparison.Ordinal) == true));
            Assert.Equal("await", owner.AwaitKeyword.Text); Assert.Equal("using", owner.UsingKeyword.Text);
            var save = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => c.Expression.ToString() == "_dbContext.SaveChangesAsync");
            Assert.True(owner.SpanStart < save.SpanStart);
            Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(), c =>
                c.Expression.ToString().EndsWith(".CommitAsync", StringComparison.Ordinal) && c.SpanStart > save.SpanStart);
        }
        // The low-level catalog lock must not become an alternative authority
        // route when another partial service/caller is added.
        var lowLevelCallers = methods.Where(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(c => c.Expression.ToString() == "BeginCatalogTransactionAsync")).Select(m => m.Identifier.ValueText).Order().ToArray();
        Assert.Equal(new[] { "BeginGlobalCatalogTransactionAsync", "BeginTenantGeographicTransactionAsync" }, lowLevelCallers);
    }
}
