using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// Executed inventory, NOT a SQL approval or provider/business acceptance test.
public sealed class App2SqlStatementInventoryTests
{
    [Fact]
    public async Task RecordEveryOriginalBoundaryStatementWithoutGrantingApproval()
    {
        var source = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT") ?? throw new InvalidOperationException("Explicit source root required.");
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned evidence root required.");
        string[] paths = [
            "PointOfSale.API/Controllers/AffiliateAuthController.cs", "PointOfSale.API/Controllers/CashboxController.cs",
            "PointOfSale.API/Development/AffiliatePhase52LocalDriver.cs", "PointOfSale.API/Infrastructure/Idempotency/ProductMutationIdempotencyService.cs",
            "PointOfSale.API/Jobs/CheckoutAttachmentCleanupJob.cs", "PointOfSale.API/Services/Admin/GeographicAdministrationService.CatalogWorkflow.cs",
            "PointOfSale.API/Services/AffiliateQaPayoutSimulation.cs", "PointOfSale.API/Services/Checkout/CheckoutAttachmentService.cs",
            "PointOfSale.API/Services/CommerceHub/Finance/CommerceHubFinancialSafetyGuardService.cs", "PointOfSale.API/Services/ProductAuthoring/ProductAuthoringOrchestrator.cs",
            "PointOfSale.API/Services/Shipping/ShippingWebhookClaimStartupValidator.cs", "PointOfSale.API/Services/Users/PosUserManagementService.cs",
            "PointOfSale.Infrastructure/Analytics/Delivery/ProviderDeliveryProcessor.cs", "PointOfSale.Infrastructure/Data/Seeding/AffiliateTaxonomySeeder.cs",
            "PointOfSale.Infrastructure/Payments/PaymentLifecycleService.CarrierRemittance.cs", "PointOfSale.Infrastructure/Services/Affiliate/AffiliateLandingPageDesignStore.cs",
            "PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.Manual.cs", "PointOfSale.Infrastructure/Services/NativeAbandonedCartRecoveryScheduler.cs",
            "PointOfSale.Infrastructure/Shipping/ShippingWebhookClaimProtocol.cs"
        ];
        HashSet<string> names = ["ExecuteUpdateAsync", "ExecuteDeleteAsync", "ExecuteSqlAsync", "ExecuteSqlRawAsync", "ExecuteSqlInterpolatedAsync"];
        var rows = new List<object>();
        for (var ordinal = 0; ordinal < paths.Length; ordinal++)
        {
            var path = paths[ordinal]; var bytes = await File.ReadAllBytesAsync(Path.Combine(source, path));
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            // Include DEBUG-only source in the inventory without constructing its driver.
            var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"]));
            var root = await tree.GetRootAsync();
            Assert.DoesNotContain(root.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => c.Expression is MemberAccessExpressionSyntax m && names.Contains(m.Name.Identifier.ValueText)).ToArray();
            var statements = calls.Select(c => new
            {
                method = c.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
                line = tree.GetLineSpan(c.Span).StartLinePosition.Line + 1,
                column = tree.GetLineSpan(c.Span).StartLinePosition.Character + 1,
                invocation = c.ToString(),
                syntax_fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.WithoutTrivia().NormalizeWhitespace().ToFullString()))),
                operation = ((MemberAccessExpressionSyntax)c.Expression).Name.Identifier.ValueText,
                sql_template = c.ArgumentList.Arguments.FirstOrDefault()?.Expression switch
                {
                    LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
                    InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(v => v is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "@parameter")),
                    _ => null
                },
                parameter_expressions = c.ArgumentList.Arguments.FirstOrDefault()?.Expression is InterpolatedStringExpressionSyntax interpolated
                    ? interpolated.Contents.OfType<InterpolationSyntax>().Select(i => i.Expression.ToString()).ToArray() : [],
                containing_method_syntax_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    c.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.WithoutTrivia().NormalizeWhitespace().ToFullString() ?? "")))
            }).ToArray();
            rows.Add(new { ordinal = ordinal + 1, path, source_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), statements,
                guard_requires_governance = SqlExecutionBoundary.RequiresMutationGovernance(text), approval = "NONE_FROM_INVENTORY",
                analysis = "Roslyn C# invocation/expression AST; SQL template is not a SQL AST or exhaustive caller/authority/provider proof" });
        }
        await using var stream = new FileStream(Path.Combine(output, "original19-statement-inventory-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, new { source, boundaries = rows, original_guard = "4f423be5-0b01-59c2-ba14-8c0f163137a5", sql20_separate = true, grants_approval = false }, new JsonSerializerOptions { WriteIndented = true });
        // Retain the original 19 / original matcher inventory above verbatim in
        // shape and numbering. Separately include read/clock primitives, SQL20
        // and new shared authority SQL so a helper extraction cannot disappear
        // from the review or be mistaken for a file-wide approval.
        HashSet<string> allNames = [.. names, "FromSql", "FromSqlRaw", "FromSqlInterpolated", "SqlQuery", "SqlQueryRaw",
            "ExecuteScalarAsync", "ExecuteReaderAsync", "ExecuteNonQueryAsync"];
        HashSet<string> sharedNames = ["HoldTenantGovernanceAsync", "ValidateFreshAccessTokenAsync", "IsCurrentActiveTenantAsync"];
        var supplements = new List<object>();
        foreach (var path in paths.Concat(new[] {
            "PointOfSale.API/Security/PosSessionAuthority.cs",
            "PointOfSale.Infrastructure/Reliability/TenantLineageAuthority.cs",
            "PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs" }))
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, path));
            var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),
                new CSharpParseOptions(preprocessorSymbols: ["DEBUG"]));
            var calls = (await tree.GetRootAsync()).DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => c.Expression is MemberAccessExpressionSyntax m && (allNames.Contains(m.Name.Identifier.ValueText) || sharedNames.Contains(m.Name.Identifier.ValueText)))
                .Select(c => new {
                    operation = ((MemberAccessExpressionSyntax)c.Expression).Name.Identifier.ValueText,
                    is_sql_primitive = allNames.Contains(((MemberAccessExpressionSyntax)c.Expression).Name.Identifier.ValueText),
                    method = c.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
                    line = tree.GetLineSpan(c.Span).StartLinePosition.Line + 1,
                    invocation = c.ToString(),
                    syntax_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.WithoutTrivia().NormalizeWhitespace().ToFullString())))
                }).ToArray();
            var index = Array.IndexOf(paths, path);
            supplements.Add(new { path, original_ordinal = index >= 0 ? (int?)(index + 1) : null,
                sql20 = path.EndsWith("/AffiliateService.cs", StringComparison.Ordinal), source_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), calls });
        }
        await using var extra = new FileStream(Path.Combine(output, "sql-all-primitives-and-shared-authority-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(extra, new { scopes = supplements, grants_approval = false,
            limit = "Exact C# invocation AST and explicit shared-helper references. Not a SQL AST, complete semantic call graph, or deployed-role/business certification." }, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(rows.Count == paths.Length, "Every original SQL boundary must be recorded exactly once.");
    }
}
