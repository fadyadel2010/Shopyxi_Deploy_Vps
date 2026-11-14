using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// New individual boundary: ONLY two parameterized identity/account lock calls,
// exercised by all22 lifecycle cases in real PostgreSQL completeSQL206/r106.
// No financial/payout/commission method is certified by this classification.
internal static class ReviewedAffiliateLifecycleSqlBoundary
{
    internal const string Path = "PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs";

    internal static bool ContainsOnlyReviewedLifecycleSql(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var sql = calls.Where(c => Name(c).StartsWith("ExecuteSql", StringComparison.Ordinal) ||
            Name(c) is "ExecuteUpdateAsync" or "ExecuteDeleteAsync" or "ExecuteUpdate" or "ExecuteDelete").ToArray();
        if (sql.Length != 2) return false;
        var helper = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.ValueText == "ReloadLockedAffiliateLifecycleAsync");
        if (helper?.Body is null || !sql.All(c => c.Ancestors().Contains(helper))) return false;
        for (var i = 0; i < sql.Length; i++)
        {
            var call = sql[i];
            if (call.Expression.ToString() != "_context.Database.ExecuteSqlInterpolatedAsync" ||
                call.ArgumentList.Arguments.Count != 2 || call.ArgumentList.Arguments[1].Expression.ToString() != "ct" ||
                call.ArgumentList.Arguments[0].Expression is not InterpolatedStringExpressionSyntax statement) return false;
            var text = new StringBuilder();
            foreach (var part in statement.Contents)
            {
                if (part is InterpolatedStringTextSyntax literal) text.Append(literal.TextToken.ValueText);
                else if (part is InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole)
                    text.Append('{').Append(hole.Expression).Append('}');
                else return false;
            }
            var expected = i == 0 ? "SELECT 1 FROM \"AffiliateUsers\" WHERE \"Id\" = {userId} FOR UPDATE" :
                "SELECT 1 FROM \"AffiliateAccounts\" WHERE \"Id\" = {affiliateAccountId} FOR UPDATE";
            if (Compact(text.ToString()) != Compact(expected)) return false;
        }

        var entrants = new[] { "ActivateAffiliateAsync", "SuspendAffiliateAsync", "CloseAffiliateAsync" };
        var admissions = calls.Where(c => Name(c) == "ReloadLockedAffiliateLifecycleAsync").ToArray();
        if (admissions.Length != 3) return false;
        foreach (var name in entrants)
        {
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == name);
            if (method?.Body is null || method.Body.Statements.FirstOrDefault() is not LocalDeclarationStatementSyntax tx ||
                !tx.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) || !tx.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return false;
            var owned = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            if (owned.Count(c => Name(c) == "BeginTransactionAsync") != 1 ||
                owned.Count(c => Name(c) == "ReloadLockedAffiliateLifecycleAsync") != 1 ||
                owned.Count(c => Name(c) == "SaveChangesAsync") != 1 || owned.Count(c => Name(c) == "CommitAsync") != 1 ||
                owned.Single(c => Name(c) == "SaveChangesAsync").SpanStart >= owned.Single(c => Name(c) == "CommitAsync").SpanStart)
                return false;
        }

        // Pin the complete source to reject unmodelled helper/caller changes.
        // This is fail-closed change detection, NOT business approval of the
        // unrelated financial methods that happen to share this source file.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal)))) ==
            "69d2fba9a8cbee8f77ea38043c2821a255bc719f05a6e5f85b885ab89d658124";
    }

    private static string Name(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
        ? member.Name.Identifier.ValueText : call.Expression.ToString();
    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");
}
