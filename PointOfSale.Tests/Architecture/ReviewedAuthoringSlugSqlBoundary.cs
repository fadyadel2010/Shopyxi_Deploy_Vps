using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// SQL10: only the exact parameterized transaction-held tenant advisory lock.
// Actual AuthorAsync commit/rollback/canceled admission and canonical SQL outbox
// evidence is retained separately. This is not approval of every authoring
// operation, deployed role, remote delivery, or another SQL statement.
internal static class ReviewedAuthoringSlugSqlBoundary
{
    internal const string Path = "PointOfSale.API/Services/ProductAuthoring/ProductAuthoringOrchestrator.cs";

    internal static bool ContainsOnlyReviewedTenantAdvisorySql(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var sql = calls.Where(c => c.Expression is MemberAccessExpressionSyntax &&
            (Name(c).StartsWith("ExecuteSql", StringComparison.Ordinal) || Name(c).StartsWith("ExecuteUpdate", StringComparison.Ordinal) ||
             Name(c).StartsWith("ExecuteDelete", StringComparison.Ordinal))).ToArray();
        if (sql.Length != 1 || sql[0].Expression.ToString() != "_context.Database.ExecuteSqlRawAsync" ||
            sql[0].ArgumentList.Arguments.Count != 3 ||
            sql[0].ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.Token.ValueText != "SELECT pg_advisory_xact_lock({0});" ||
            sql[0].ArgumentList.Arguments[1].Expression is not ArrayCreationExpressionSyntax array ||
            array.Initializer is not { } initializer || initializer.Expressions.Count != 1 || initializer.Expressions[0].ToString() != "lockKey" ||
            sql[0].ArgumentList.Arguments[2].Expression.ToString() != "cancellationToken") return false;
        var helper = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.ValueText == "BeginSlugAuthorityTransactionAsync");
        if (helper?.Body == null || !sql[0].Ancestors().Contains(helper)) return false;
        var begin = helper.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => Name(c) == "BeginTransactionAsync").ToArray();
        var ownedTry = helper.Body.Statements.OfType<TryStatementSyntax>().SingleOrDefault();
        if (begin.Length != 1 || begin[0].Expression.ToString() != "_context.Database.BeginTransactionAsync" ||
            begin[0].ArgumentList.Arguments.Count != 2 ||
            begin[0].ArgumentList.Arguments[0].Expression.ToString() != "IsolationLevel.ReadCommitted" ||
            begin[0].ArgumentList.Arguments[1].Expression.ToString() != "cancellationToken" ||
            begin[0].SpanStart >= sql[0].SpanStart || ownedTry == null || !sql[0].Ancestors().Contains(ownedTry.Block) ||
            ownedTry.Catches.Count != 1 || !ownedTry.Catches[0].Block.Statements.OfType<ThrowStatementSyntax>().Any() ||
            !ownedTry.Catches[0].DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c => c.Expression.ToString() == "transaction.DisposeAsync")) return false;
        var callers = calls.Where(c => Name(c) == "BeginSlugAuthorityTransactionAsync").ToArray();
        if (callers.Length != 2 || !callers.Select(c => c.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText)
            .OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "ExecuteCreateAsync", "ExecuteUpdateAsync" })) return false;
        foreach (var call in callers)
        {
            var declaration = call.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
            if (call.ArgumentList.Arguments.Count != 2 || call.ArgumentList.Arguments[0].Expression.ToString() != "tenantId" ||
                call.ArgumentList.Arguments[1].Expression.ToString() != "cancellationToken" || declaration == null ||
                !declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) || !declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return false;
        }
        // Whole-file pin includes tenant-derived SHA256 lock key, all callers,
        // disposal/commit and publication paths. Unknown helpers/SQL/control-flow
        // changes refuse even if the narrow syntax above still happens to match.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal)))) ==
            "61edb5a2986259c9172f2bcb242514307b1e6601841ef8caba32423547f93764";
    }

    private static string Name(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
        ? member.Name.Identifier.ValueText : call.Expression.ToString();
}
