using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// SQL11: the single constant SQL statement is read-only synchronization. The
// exact database core has real PostgreSQL rollback-only proof. This does NOT
// certify cold service construction, applied migrations, or production startup.
internal static class ReviewedShippingReadinessSqlBoundary
{
    internal const string Path = "PointOfSale.API/Services/Shipping/ShippingWebhookClaimStartupValidator.cs";

    internal static bool IsReadOnlyDatabaseBoundary(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var operations = calls.Where(c => Name(c).StartsWith("Execute", StringComparison.Ordinal) ||
            Name(c).StartsWith("SaveChanges", StringComparison.Ordinal) ||
            Name(c).StartsWith("Migrate", StringComparison.Ordinal) || Name(c) == "CommitAsync").ToArray();
        if (operations.Length != 1 || operations[0].Expression.ToString() != "db.Database.ExecuteSqlRawAsync" ||
            operations[0].ArgumentList.Arguments.Count != 2 ||
            operations[0].ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.Token.ValueText != "SET TRANSACTION READ ONLY" ||
            operations[0].ArgumentList.Arguments[1].Expression.ToString() != "probe.Token") return false;

        var core = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == "ValidateDatabaseAsync");
        var body = core?.Body?.Statements.OfType<TryStatementSyntax>().SingleOrDefault();
        if (body?.Finally is null || !operations[0].Ancestors().Contains(body.Block)) return false;
        var begin = calls.Where(c => Name(c) == "BeginTransactionAsync").ToArray();
        var rollback = calls.Where(c => Name(c) == "RollbackAsync").ToArray();
        var dispose = calls.Where(c => Name(c) == "DisposeAsync").ToArray();
        if (begin.Length != 1 || rollback.Length != 1 || dispose.Length != 1 ||
            begin[0].Expression.ToString() != "db.Database.BeginTransactionAsync" ||
            begin[0].SpanStart >= operations[0].SpanStart ||
            rollback[0].Expression.ToString() != "transaction.RollbackAsync" ||
            rollback[0].ArgumentList.Arguments.SingleOrDefault()?.Expression.ToString() != "cleanup.Token" ||
            !rollback[0].Ancestors().Contains(body.Finally) || !dispose[0].Ancestors().Contains(body.Finally)) return false;

        // Whole-file pin prevents a helper/deferred/unknown operation from being
        // smuggled past the narrow statement checks. New code needs new review.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal)))) ==
            "f3559d6d30db8192ebfc3b6b46bc1e47792970dda0cae806542ed57db4e38410";
    }

    private static string Name(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
        ? member.Name.Identifier.ValueText : call.Expression.ToString();
}
