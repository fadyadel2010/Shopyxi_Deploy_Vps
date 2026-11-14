using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

public sealed class ReviewedSqlStatementContinuationTests
{
    [Fact]
    public void OnlyThe88IndividuallyPinnedHistoricalStatementsContinueWithinTheirUnchangedSources()
    {
        Assert.Equal(16, ReviewedSqlStatementContinuations.Entries.Count);
        Assert.Equal(88, ReviewedSqlStatementContinuations.Entries.Sum(e => e.Value.Length));
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")!;
        foreach (var contract in ReviewedSqlGovernanceBoundaries.Contracts)
        {
            var source = File.ReadAllText(Path.Combine(root, contract.Path));
            Assert.Equal(contract.StatementCount, ReviewedSqlStatementContinuations.Entries[contract.Path].Length);
            Assert.True(ReviewedSqlStatementContinuations.Exact(contract.Path, source, ReviewedSqlStatementContinuations.ReviewedContext));
            var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRoot();
            var first = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().First(ReviewedSqlGovernanceBoundaries.IsSqlPrimitive);
            var changed = source.Remove(first.SpanStart, first.Span.Length).Insert(first.SpanStart, "unreviewed.ExecuteSqlRawAsync(\"DELETE FROM UnreviewedAuthority\")");
            Assert.False(ReviewedSqlStatementContinuations.Exact(contract.Path, changed, ReviewedSqlStatementContinuations.ReviewedContext));
            Assert.False(ReviewedSqlStatementContinuations.Exact(contract.Path, source, ReviewedSqlStatementContinuations.ReviewedContext + "unreviewed-caller"));
        }
    }

    [Fact]
    public void GlobalContextSealAloneCannotApproveNewGenerationSqlOrUnknownSource()
    {
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")!;
        const string path = "PointOfSale.Infrastructure/Marketplace/Generation/MarketplaceGenerationGatewayJournal.cs";
        var source = File.ReadAllText(Path.Combine(root, path));
        Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, source, ReviewedSqlGovernanceBoundaries.ProductionContext));
        Assert.False(ReviewedSqlStatementContinuations.Exact(path, source, ReviewedSqlGovernanceBoundaries.ProductionContext));
        Assert.NotEqual(ReviewedSqlGovernanceBoundaries.HistoricalProductionContext, ReviewedSqlGovernanceBoundaries.ProductionContext);
    }
}
