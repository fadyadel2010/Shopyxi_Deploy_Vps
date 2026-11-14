using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

/// <summary>Local Roslyn execution/context inventory, NOT a SQL/provider approval.</summary>
public sealed class GenerationSqlStatementInventoryTests
{
    [Fact]
    public async Task RecordExactStatementAstParametersAndTransactionContextsWithoutGrantingApproval()
    {
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")!;
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR")!;
        var paths = ReviewedSqlGovernanceBoundaries.Contracts.Select(c => c.Path)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "PointOfSale.Infrastructure/Marketplace/Generation"), "*.cs")
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))).Distinct().Order(StringComparer.Ordinal);
        var files = new List<object>();
        foreach (var path in paths)
        {
            var source = ReviewedSqlGovernanceBoundaries.Normalize(await File.ReadAllTextAsync(Path.Combine(root, path)));
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"]));
            var syntax = await tree.GetRootAsync();
            Assert.DoesNotContain(syntax.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            var boundaries = syntax.DescendantNodes().Where(node =>
                node is InvocationExpressionSyntax call && ReviewedSqlGovernanceBoundaries.IsSqlPrimitive(call) ||
                node is ObjectCreationExpressionSyntax create && create.Type.ToString() == "NpgsqlCommand" ||
                node is AssignmentExpressionSyntax assign && assign.Left is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "CommandText" ||
                node is VariableDeclaratorSyntax variable && variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                    literal.Token.ValueText.Contains("CREATE TABLE marketplace_generation.", StringComparison.Ordinal))
                .Select(node => new
                {
                    kind = node.Kind().ToString(), line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                    ast = node.WithoutTrivia().NormalizeWhitespace().ToFullString(),
                    ast_sha256 = ReviewedSqlGovernanceBoundaries.Hash(node.WithoutTrivia().NormalizeWhitespace().ToFullString()),
                    method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
                    complete_execution_context = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ToFullString(),
                    sql_input = node switch
                    {
                        ObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString(),
                        AssignmentExpressionSyntax assignment => assignment.Right.ToString(),
                        VariableDeclaratorSyntax variable => variable.Initializer?.Value is LiteralExpressionSyntax l ? l.Token.ValueText : null,
                        _ => null
                    }
                }).ToArray();
            if (boundaries.Length > 0) files.Add(new { path, normalized_source_sha256 = ReviewedSqlGovernanceBoundaries.Hash(source), boundaries });
        }
        await using var stream = new FileStream(Path.Combine(output, "generation-sql-exact-inputs.json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, new { files, grants_approval = false,
            ast_scope = "Roslyn C# constructor/invocation/assignment AST and complete method/parameter/transaction context; SQL bodies retained verbatim, not falsely described as a PostgreSQL parser AST." }, new JsonSerializerOptions { WriteIndented = true });
    }
}
