using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

internal static class MarketplaceRepairAuthorityCallPath
{
    public static void AssertLiveVisibilityControlsConvergence()
    {
        var converge = Method(Parse("PointOfSale.Infrastructure/Jobs/ElasticsearchDriftReconciliationJob.cs"), "ConvergeOneAsync");
        var live = Assert.Single(converge.DescendantNodes().OfType<VariableDeclaratorSyntax>(), node => node.Identifier.ValueText == "live");
        var predicate = Assert.IsType<InvocationExpressionSyntax>(live.Initializer!.Value);
        Assert.True(Same(predicate.Expression, "IsLiveSqlLineage"));
        Assert.Equal(new[] { "service", "tenant", "profile", "vendor" },
            predicate.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()));
        var guard = Assert.Single(converge.DescendantNodes().OfType<IfStatementSyntax>(), node => Same(node.Condition, "!live"));
        Assert.True(guard.SpanStart > live.Span.End);
        Assert.Contains(guard.DescendantNodes().OfType<ReturnStatementSyntax>(), node =>
            node.Expression is MemberAccessExpressionSyntax outcome && Same(outcome, "Outcome.Deleted"));
        Assert.Contains(guard.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "DeleteAsync");
        var writes = converge.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax member && member.Expression.ToString() == "_elasticClient" &&
            member.Name.Identifier.ValueText is "UpdateAsync" or "IndexAsync").ToArray();
        Assert.Equal(2, writes.Length);
        Assert.All(writes, call => Assert.True(call.SpanStart > guard.Span.End));
    }

    // These are call/data-flow checks, not permission to write raw indices. The unchanged
    // raw-writer guard and real PostgreSQL/OpenSearch repair matrix remain independent gates.
    public static void AssertSqlBusinessDataRecomposition()
    {
        var controller = Parse("PointOfSale.API/Controllers/MarketplaceController.cs");
        var job = Parse("PointOfSale.Infrastructure/Jobs/ElasticsearchDriftReconciliationJob.cs");
        var endpoint = Method(controller, "RepairServicesAsync");
        Assert.Contains(endpoint.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            Same(call.Expression, "_repairAuthority.RepairAsync") && call.ArgumentList.Arguments.Count == 2);

        var converge = Method(job, "ConvergeOneAsync");
        var requiredDeclarations = new HashSet<string>(StringComparer.Ordinal)
            { "trust", "requests", "total", "completed", "fields", "trustFields" };
        var declarations = converge.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(node => requiredDeclarations.Contains(node.Identifier.ValueText))
            .ToDictionary(node => node.Identifier.ValueText, StringComparer.Ordinal);
        var trust = Assert.IsType<AwaitExpressionSyntax>(declarations["trust"].Initializer!.Value);
        var trustCall = Assert.IsType<InvocationExpressionSyntax>(trust.Expression);
        Assert.True(Same(trustCall.Expression, "_trust.GetTrustAggregateAsync"));
        Assert.True(Same(trustCall.ArgumentList.Arguments[0].Expression, "service.ProfileId"));
        Assert.Contains(converge.DescendantNodes().OfType<IfStatementSyntax>(), node =>
            node.Condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(condition =>
                condition.IsKind(SyntaxKind.EqualsExpression) && Same(condition.Left, "trust") &&
                condition.Right.IsKind(SyntaxKind.NullLiteralExpression)) &&
            node.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());

        var requests = declarations["requests"].Initializer!.Value;
        foreach (var comparison in new[] { "r.ServiceId == id", "r.TenantId == service.TenantId", "r.ProviderProfileId == service.ProfileId" })
            Assert.Contains(requests.DescendantNodes().OfType<BinaryExpressionSyntax>(), node => Same(node, comparison));
        Assert.Contains(requests.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>(), node => Same(node, "!r.IsDeleted"));
        foreach (var name in new[] { "total", "completed" })
        {
            var count = Assert.IsType<InvocationExpressionSyntax>(Assert.IsType<AwaitExpressionSyntax>(declarations[name].Initializer!.Value).Expression);
            Assert.True(Same(count.Expression, "requests.CountAsync"));
        }
        Assert.Contains(declarations["completed"].DescendantNodes().OfType<BinaryExpressionSyntax>(), node =>
            Same(node, "r.Status == ServiceRequestStatus.CompletedByBuyer"));

        var fields = declarations["fields"].Initializer!.Value;
        var statistics = Assert.Single(fields.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>());
        Assert.True(Same(Assert.Single(statistics.Initializers, node => node.NameEquals?.Name.Identifier.ValueText == "totalRequests").Expression, "total"));
        Assert.True(Same(Assert.Single(statistics.Initializers, node => node.NameEquals?.Name.Identifier.ValueText == "completedRequests").Expression, "completed"));
        var trustFields = declarations["trustFields"].Initializer!.Value;
        foreach (var field in new[] { "AverageRating", "TotalReviews", "MarketingReviewCount", "PosVerifiedCount", "MarketingWeightedScore" })
            Assert.Contains(trustFields.DescendantNodes().OfType<MemberAccessExpressionSyntax>(), node => Same(node, "trust." + field));

        Assert.Contains(converge.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Doc" &&
            call.ArgumentList.Arguments.Any(argument => Same(argument.Expression, "fields")));
        Assert.Contains(converge.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "IndexAsync" &&
            Same(call.ArgumentList.Arguments[0].Expression, "fields"));
    }

    private static bool Same(Microsoft.CodeAnalysis.SyntaxNode actual, string expected) =>
        SyntaxFactory.AreEquivalent(actual, SyntaxFactory.ParseExpression(expected));

    private static MethodDeclarationSyntax Method(CompilationUnitSyntax root, string name) =>
        Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(), node => node.Identifier.ValueText == name);

    private static CompilationUnitSyntax Parse(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PointOfSale.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)))).GetCompilationUnitRoot();
    }
}
