using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

internal static class ExtractedAuthorityCallPaths
{
    private static SyntaxNode Root(string path) => CSharpSyntaxTree.ParseText(CertificationSource.ReadRepoFile(path)).GetRoot();
    private static string Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty
    };

    internal static void AssertProductIdempotencyDelegation()
    {
        var controller = Root("PointOfSale.API/Controllers/ProductVariantsController.cs");
        var calls = controller.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => Name(c) == "TryClaimAsync").ToArray();
        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.Equal("_productMutationIdempotency", Assert.IsType<MemberAccessExpressionSyntax>(call.Expression).Expression.ToString());
            var request = call.ArgumentList.Arguments[0].Expression.DescendantNodesAndSelf()
                .OfType<ObjectCreationExpressionSyntax>().Single(c => c.Type.ToString() == "ProductMutationIdempotencyRequest");
            var tenant = request.ArgumentList!.Arguments[0].Expression;
            if (tenant is IdentifierNameSyntax local)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().First();
                var declaration = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Single(v => v.Identifier.ValueText == local.Identifier.ValueText);
                Assert.True(declaration.SpanStart < call.SpanStart);
                Assert.Equal("_tenantContext.RequiredTenantId", declaration.Initializer!.Value.ToString());
                Assert.DoesNotContain(method.DescendantNodes().OfType<AssignmentExpressionSyntax>(), a => a.Left.ToString() == local.Identifier.ValueText);
            }
            else Assert.Equal("_tenantContext.RequiredTenantId", tenant.ToString());
        }
        var service = Root("PointOfSale.API/Infrastructure/Idempotency/ProductMutationIdempotencyService.cs");
        var claim = service.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryClaimAsync");
        var inserts = claim.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => Name(c) == "ExecuteSqlInterpolatedAsync").ToArray();
        Assert.Equal(3, inserts.Length);
        Assert.All(inserts, c => Assert.IsType<InterpolatedStringExpressionSyntax>(c.ArgumentList.Arguments[0].Expression));
        Assert.Contains(inserts, c => c.ToString().Contains("ON CONFLICT (\"\"TenantId\"\", \"\"Key\"\", \"\"Operation\"\") DO NOTHING", StringComparison.Ordinal));
        Assert.Contains(inserts, c => c.ToString().Contains("WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal));
        foreach (var methodName in new[] { "TryRenewClaimLeaseAsync", "TryWriteIncompleteClaimStateAsync", "TryCompleteDurablyAsync" })
        {
            var method = service.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == methodName);
            Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => Name(c) == "ExecuteUpdateAsync");
            var predicates = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => Name(c) == "Where").ToArray();
            Assert.Contains(predicates, p => p.ToString().Contains("existing.TenantId == record.TenantId", StringComparison.Ordinal) &&
                p.ToString().Contains("existing.UpdatedAt == expectedLeaseUpdatedAtUtc", StringComparison.Ordinal) &&
                p.ToString().Contains("existing.ResponseStatusCode == null", StringComparison.Ordinal));
        }
    }

    internal static void AssertSqlOwnedAffiliateDetail()
    {
        var root = Root("PointOfSale.API/Controllers/AffiliatePortalController.cs");
        var detail = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetProductDetail");
        var owner = detail.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.ValueText == "owner");
        Assert.Contains(owner.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => Name(c) == "SingleOrDefaultAsync");
        Assert.Contains(owner.DescendantNodes().OfType<MemberAccessExpressionSyntax>(), m => m.ToString() == "_dbContext.Products");
        var doc = detail.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.ValueText == "doc");
        var conditional = Assert.IsType<ConditionalExpressionSyntax>(doc.Initializer!.Value);
        Assert.Equal("owner == null", conditional.Condition.ToString());
        Assert.True(conditional.WhenTrue.IsKind(SyntaxKind.NullLiteralExpression));
        var call = conditional.WhenFalse.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Single();
        Assert.Equal("RecomposeAffiliateIntelligenceDocumentBySqlAuthorityAsync", Name(call));
        var seed = Assert.IsType<ObjectCreationExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        var assignments = seed.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>().ToDictionary(a => a.Left.ToString(), a => a.Right.ToString());
        Assert.Equal("productId", assignments["ProductId"]);
        Assert.Equal("owner.TenantId", assignments["TenantId"]);
        Assert.DoesNotContain(detail.DescendantNodes().OfType<MemberAccessExpressionSyntax>(), m => m.ToString().StartsWith("_mongoDbContext.", StringComparison.Ordinal));
        Assert.Contains(detail.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => Name(c) == "NotFound");
    }

    internal static void AssertMongoOnlyStorefrontConsumers()
    {
        // Assert the actual dependency and side-effect boundary, not a Redis marker
        // where no Redis write exists. Existing SQL/freshness/Mongo checks remain.
        foreach (var path in new[] { "StorefrontConfigProjectionConsumer.cs", "NavigationProjectionConsumer.cs" })
        {
            var root = Root("PointOfSale.Infrastructure/Consumers/" + path);
            Assert.DoesNotContain(root.DescendantNodes().OfType<UsingDirectiveSyntax>(), u =>
                u.Name?.ToString().Contains("Redis", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(root.DescendantNodes().OfType<ParameterSyntax>(), p =>
                p.Type?.ToString().Contains("Redis", StringComparison.OrdinalIgnoreCase) == true ||
                p.Type?.ToString() is "IConnectionMultiplexer" or "IDistributedCache");
            Assert.DoesNotContain(root.DescendantNodes().OfType<InvocationExpressionSyntax>(), c =>
                Name(c) is "StringSetAsync" or "KeyDeleteAsync" or "GetDatabase" or "SetStringAsync" or "RemoveAsync");
            var replacements = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => Name(c) == "ReplaceOneAsync").ToArray();
            Assert.NotEmpty(replacements);
            foreach (var replace in replacements)
            {
                var method = replace.Ancestors().OfType<MethodDeclarationSyntax>().First();
                var guardName = path.StartsWith("Navigation", StringComparison.Ordinal) ? "RequireReviewedNavigationMutation" : "RequireReviewedStoreConfigMutation";
                Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => Name(c) == guardName && c.SpanStart < replace.SpanStart);
                Assert.Contains(replace.ArgumentList.Arguments, a => a.Expression.ToString().Contains("Filter", StringComparison.OrdinalIgnoreCase));
            }
        }
        foreach (var path in new[] { "StorefrontStoreConfigController.cs", "StorefrontNavigationController.cs" })
        {
            var root = Root("PointOfSale.API/Controllers/" + path);
            Assert.DoesNotContain(root.DescendantNodes().OfType<ParameterSyntax>(), p =>
                p.Type?.ToString() is "IConnectionMultiplexer" or "IDistributedCache" or "IMongoDbContext");
            Assert.Contains(root.DescendantNodes().OfType<MemberAccessExpressionSyntax>(), m => m.Name.Identifier.ValueText == "StoreConfigs");
        }
    }
}
