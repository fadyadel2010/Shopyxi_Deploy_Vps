using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

public sealed class ReviewedSqlGovernanceBoundaryTests
{
    public static IEnumerable<object[]> Boundaries => ReviewedSqlGovernanceBoundaries.Contracts.Select(c => new object[] { c.Path });

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void ExactStatementsAndCallerContextAreRequired_NotContainingServiceApproval(string path)
    {
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, path));
        var context = ReviewedSqlGovernanceBoundaries.ReadProductionContext(root);
        Assert.Equal(ReviewedSqlGovernanceBoundaries.ProductionContext, context);
        Assert.True(ReviewedSqlGovernanceBoundaries.IsApproved(path, source, context));
        Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, source, context + "changed-caller"));
        Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved("unreviewed/" + path, source, context));
        Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, source + "\n// changed helper/caller/authority review required\n", context));
        Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, "not C#", context));
        Assert.True(ReviewedSqlGovernanceBoundaries.IsApproved(path,
            ReviewedSqlGovernanceBoundaries.Normalize(source).Replace("\n", "\r\n", StringComparison.Ordinal), context));

        // Every execution primitive is individually perturbed, including reads
        // and clocks missed by the original mutation-only matcher. No new SQL,
        // different parameter/lock/clock or extracted helper can inherit approval.
        var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRoot();
        var original = ReviewedSqlGovernanceBoundaries.Statements(source);
        var tested = 0;
        foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(ReviewedSqlGovernanceBoundaries.IsSqlPrimitive))
        {
            var replacement = source.Remove(call.SpanStart, call.Span.Length)
                .Insert(call.SpanStart, "unreviewed.ExecuteSqlRawAsync(\"DELETE FROM UnreviewedAuthority\")");
            var changed = ReviewedSqlGovernanceBoundaries.Statements(replacement);
            Assert.NotEqual(original, changed);
            tested++;
            Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, replacement, context));
        }
        Assert.Equal(ReviewedSqlGovernanceBoundaries.Contracts.Single(c => c.Path == path).StatementCount, tested);
        foreach (var marker in new[] { "FOR UPDATE", "FOR SHARE", "TenantId", "BeginTransactionAsync", "CommitAsync", "SaveChangesAsync",
            "AsNoTracking", "ReloadAsync", "clock_timestamp", "TokensValidAfterUtc", "CancellationToken", "Outbox", "Audit" })
        {
            var changed = source.Replace(marker, "UnreviewedAuthority", StringComparison.Ordinal);
            if (changed != source) Assert.False(ReviewedSqlGovernanceBoundaries.IsApproved(path, changed, context));
        }
    }

    [Fact]
    public void ContextPinsCallerContentIdentityAndCompleteFileSet()
    {
        (string Path, string SourceHash)[] rows = [("API/caller.cs", "caller"), ("Core/authority.cs", "authority")];
        var expected = ReviewedSqlGovernanceBoundaries.ContextHash(rows);
        Assert.Equal(expected, ReviewedSqlGovernanceBoundaries.ContextHash(rows.Reverse()));
        Assert.NotEqual(expected, ReviewedSqlGovernanceBoundaries.ContextHash(rows.Take(1)));
        Assert.NotEqual(expected, ReviewedSqlGovernanceBoundaries.ContextHash(rows.Append(("API/new-caller.cs", "caller"))));
        Assert.NotEqual(expected, ReviewedSqlGovernanceBoundaries.ContextHash([(rows[0].Path, "changed-session-authority"), rows[1]]));
        Assert.NotEqual(expected, ReviewedSqlGovernanceBoundaries.ContextHash([("API/renamed-caller.cs", rows[0].SourceHash), rows[1]]));
        Assert.Equal(16, ReviewedSqlGovernanceBoundaries.Contracts.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ReviewedSqlGovernanceBoundaries.Contracts, c => c.OriginalOrdinal is 3 or 10 or 11 or 14 || c.Path.EndsWith("/AffiliateService.cs", StringComparison.Ordinal));
    }

    private static string SourceRoot() => Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")
        ?? throw new InvalidOperationException("Explicit source root required for versioned SQL governance controls.");
}
