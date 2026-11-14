using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

internal static class CommerceHubAuthorityDiscoveryCallPath
{
    internal static void AssertProductionPipeline()
    {
        var main = CSharpSyntaxTree.ParseText(CertificationSource.ReadRepoFile(
            "PointOfSale.API/Services/CommerceHub/CommerceHubQueryService.cs")).GetRoot();
        var authority = CSharpSyntaxTree.ParseText(CertificationSource.ReadRepoFile(
            "PointOfSale.API/Services/CommerceHub/CommerceHubQueryService.AuthoritySearch.cs")).GetRoot();
        var methods = main.DescendantNodes().Concat(authority.DescendantNodes())
            .OfType<MethodDeclarationSyntax>().Where(method => method.Body != null || method.ExpressionBody != null)
            .GroupBy(method => method.Identifier.ValueText)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var name in new[] { "GetProductsAsync", "SearchAsync" })
        {
            var method = methods[name];
            Assert.Contains(Calls(method), call => CallName(call) == "BeginAuthoritySnapshotAsync");
            Assert.Contains(Calls(method), call => CallName(call) == "ExecuteSearchAsync");
            Assert.DoesNotContain(Calls(method), call => CallName(call) == "RecomposeVisibleListingItemsAsync");
            var total = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Single(assignment => assignment.Left is IdentifierNameSyntax identifier &&
                    identifier.Identifier.ValueText == (name == "SearchAsync" ? "Total" : "TotalCount") &&
                    assignment.Right is MemberAccessExpressionSyntax);
            var value = Assert.IsType<MemberAccessExpressionSyntax>(total.Right);
            Assert.Equal("result", Assert.IsType<IdentifierNameSyntax>(value.Expression).Identifier.ValueText);
            Assert.Equal("TotalCount", value.Name.Identifier.ValueText);
        }
        var execute = methods["ExecuteSearchAsync"];
        Assert.Contains(Calls(execute), call => CallName(call) == "SearchSqlAuthorityFirstAsync");
        Assert.DoesNotContain(Calls(execute), call => CallName(call) == "SearchPublishedProductsAsync");
        var pipeline = methods["SearchSqlAuthorityFirstAsync"];
        Assert.DoesNotContain(Calls(pipeline), call => CallName(call) is "Find" or "CountDocumentsAsync" or "SearchPublishedProductsAsync");
        var batch = pipeline.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Single(statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "batch"));
        Assert.Contains(Calls(batch), call => CallName(call) == "Take" &&
            call.ArgumentList.Arguments.Single().Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "batchSize");
        Assert.Contains(Calls(pipeline), call => CallName(call) == "GetVendorsSuspendedFromNewOrdersAsync");
        Assert.Contains(Calls(pipeline), call => CallName(call) == "MatchesAuthorityFilters");
        var facets = Calls(methods["SearchAsync"]).Single(call => CallName(call) == "MapFacetsAsync");
        Assert.Equal("result", Assert.IsType<IdentifierNameSyntax>(facets.ArgumentList.Arguments[0].Expression).Identifier.ValueText);
        Assert.Contains(Calls(methods["BuildZeroResultSuggestionsAsync"]), call => CallName(call) == "SearchSqlAuthorityFirstAsync");
        Assert.DoesNotContain(Calls(methods["BuildZeroResultSuggestionsAsync"]), call => CallName(call) == "GetZeroResultSuggestionsAsync");
    }

    private static IEnumerable<InvocationExpressionSyntax> Calls(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>();

    private static string CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty
    };
}
