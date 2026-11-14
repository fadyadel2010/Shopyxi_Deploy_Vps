using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

public sealed class App2MarketplaceInventoryTests
{
    [Fact]
    public async Task CensusAllProductionSyntaxTreesAndRecordSourceAndSearchBoundariesWithoutApprovingThem()
    {
        var source = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT") ?? throw new InvalidOperationException("Explicit source required.");
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned evidence required.");
        string[] subjects = ["Service", "MarketplaceProfile", "ServiceRequest", "ServiceReview", "MarketingReviewEvidence", "ProviderTrustAggregate", "Tenant", "UserGovernanceProfile"];
        HashSet<string> storageCalls = ["SaveChanges", "SaveChangesAsync", "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync", "ExecuteSqlRawAsync", "ExecuteSqlInterpolatedAsync", "ExecuteNonQueryAsync", "WriteAsync", "BeginBinaryImportAsync"];
        HashSet<string> searchCalls = ["SearchAsync", "ScrollAsync", "IndexAsync", "UpdateAsync", "DeleteAsync", "BulkAsync", "DoRequestAsync", "RefreshAsync", "ReindexAsync"];
        var files = new List<object>(); var boundaries = new List<object>();
        foreach (var project in new[] { "PointOfSale.API", "PointOfSale.Core", "PointOfSale.Infrastructure" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(source, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
        {
            var bytes = await File.ReadAllBytesAsync(file); var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var root = await CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRootAsync();
            var path = Path.GetRelativePath(source, file).Replace('\\', '/');
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            files.Add(new { path, source_sha256 = hash, syntax_errors = root.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error) });
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(call => new
                {
                    name = call.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : call.Expression.ToString(),
                    expression = call.ToString(), line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                }).ToArray();
                var identifiers = method.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);
                var referenced = subjects.Where(s => identifiers.Contains(s) || identifiers.Contains(s + "s")).ToArray();
                var isSearch = calls.Any(c => searchCalls.Contains(c.name));
                if (referenced.Length == 0 && !isSearch && !path.Contains("Marketplace", StringComparison.Ordinal)) continue;
                boundaries.Add(new
                {
                    path, source_sha256 = hash, method = method.Identifier.ValueText,
                    line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    source_entities = referenced, storage_calls = calls.Where(c => storageCalls.Contains(c.name)).ToArray(),
                    search_calls = calls.Where(c => searchCalls.Contains(c.name)).ToArray(),
                    all_calls = calls,
                    syntax_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(method.WithoutTrivia().NormalizeWhitespace().ToFullString()))),
                    protocol_participation = "REQUIRES_CALLER_AND_ROLE_REVIEW_NOT_INFERRED_FROM_TEXT"
                });
            }
        }
        await using var stream = new FileStream(Path.Combine(output, "marketplace-source-search-census-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, new
        {
            source, files, boundaries,
            coverage = "Every non-generated C# file in three production projects, DEBUG included; method/entity/call syntax census only, not whole-program alias/dataflow or executed call-graph proof",
            omissions_certified_absent = false, all_writer_admission_certified = false,
            reader_pinning_certified = false, physical_role_authority_certified = false, recovery_enabled = false
        }, new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEmpty(files); Assert.NotEmpty(boundaries);
    }
}
