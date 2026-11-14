using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// Individual SQL14 review, not a filename exemption. The exact source and fixed
// catalog were exercised by all eight AffiliateTaxonomySeederProviderTests on
// disposable PostgreSQL18 (complete SQL158/r82). Any source change requires a
// new review/provider proof, even when these additional shape checks still pass.
internal static class ReviewedTaxonomySeederSqlBoundary
{
    internal const string Path = "PointOfSale.Infrastructure/Data/Seeding/AffiliateTaxonomySeeder.cs";
    internal const string CatalogPath = "PointOfSale.Infrastructure/Data/Seeding/AffiliateTaxonomyCatalog.cs";

    internal static bool IsApproved(string source, string catalog)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var mutations = calls.Where(call => Name(call).StartsWith("Execute", StringComparison.Ordinal) ||
            Name(call).StartsWith("SaveChanges", StringComparison.Ordinal)).ToArray();
        if (mutations.Length != 1 || Name(mutations[0]) != "ExecuteSqlAsync" ||
            mutations[0].Expression.ToString() != "_dbContext.Database.ExecuteSqlAsync" ||
            mutations[0].ArgumentList.Arguments.Count != 2 ||
            mutations[0].ArgumentList.Arguments[1].Expression.ToString() != "cancellationToken" ||
            mutations[0].ArgumentList.Arguments[0].Expression is not InterpolatedStringExpressionSyntax sql)
            return false;

        var statement = new StringBuilder();
        foreach (var part in sql.Contents)
        {
            if (part is InterpolatedStringTextSyntax text) statement.Append(text.TextToken.ValueText);
            else if (part is InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole)
                statement.Append('{').Append(hole.Expression).Append('}');
            else return false;
        }
        const string expectedSql = """
            INSERT INTO "AffiliateCategories"
                ("Id", "Name", "Slug", "ParentId", "Level", "IsActive", "IsDeleted", "DisplayOrder", "CreatedAt")
            VALUES
                ({entry.Id}, {entry.Name}, {entry.Slug}, {entry.ParentId}, {entry.Level}, TRUE, FALSE, {entry.DisplayOrder}, {createdAt})
            ON CONFLICT DO NOTHING
            """;
        if (Compact(statement.ToString()) != Compact(expectedSql)) return false;

        var seed = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == "SeedAsync");
        if (seed?.Body is null || seed.Body.Statements.Count != 7 ||
            seed.Body.Statements[2] is not LocalDeclarationStatementSyntax transaction ||
            !transaction.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) || !transaction.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) ||
            seed.Body.Statements[3] is not ForEachStatementSyntax { Statement: BlockSyntax loop } || loop.Statements.Count != 3 ||
            loop.Statements[0] is not IfStatementSyntax || loop.Statements[2] is not IfStatementSyntax ||
            Compact(seed.Body.Statements[4].ToString()) != "awaitEnsureBaselineIdentityAsync(entries,cancellationToken);" ||
            Compact(seed.Body.Statements[5].ToString()) != "awaittransaction.CommitAsync(cancellationToken);")
            return false;
        if (calls.Count(call => Name(call) == "BeginTransactionAsync") != 1 ||
            calls.Count(call => Name(call) == "CommitAsync") != 1) return false;

        // Pins close unmodelled control-flow/helper/catalog changes rather than
        // trusting comments or a few approving substrings. LF normalization only
        // permits checkout newline differences, not SQL or behavior changes.
        return Digest(source) == "c297d9acf4885e787d63eed6268eee5a8a740d03f07a6b5d9e4dd9b53a82e1e7" &&
            Digest(catalog) == "0d62e423dae7bc6a176b74a475e4248f83cc82155407ad91fb59b95a20df2cf7";
    }

    private static string Name(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
        ? member.Name.Identifier.ValueText : call.Expression.ToString();
    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");
    private static string Digest(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal))));
}
