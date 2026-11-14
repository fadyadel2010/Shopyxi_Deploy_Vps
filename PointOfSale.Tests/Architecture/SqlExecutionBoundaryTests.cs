using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

internal static class SqlExecutionBoundary
{
    private static readonly HashSet<string> ExecuteNames = ["ExecuteUpdateAsync", "ExecuteDeleteAsync", "ExecuteSqlAsync", "ExecuteSqlRawAsync", "ExecuteSqlInterpolatedAsync"];
    private static string CallName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : string.Empty;
    internal static bool RequiresMutationGovernance(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return true; // Unknown/unparseable input is never approved.
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => ExecuteNames.Contains(CallName(c))))
        {
            var name = CallName(call);
            if (name is "ExecuteUpdateAsync" or "ExecuteDeleteAsync" || call.ArgumentList.Arguments.Count == 0) return true;
            var argument = call.ArgumentList.Arguments[0].Expression;
            string? sql = argument switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                InterpolatedStringExpressionSyntax text => string.Concat(text.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "?")),
                _ => null
            };
            if (sql == null) return true;
            var normalized = Regex.Replace(sql.Trim(), @"\s+", " ");
            var readOnly = normalized.Equals("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase);
            var advisory = Regex.IsMatch(normalized, @"\ASELECT pg_advisory_xact_lock\(\?\)\z", RegexOptions.IgnoreCase) &&
                name is "ExecuteSqlAsync" or "ExecuteSqlInterpolatedAsync";
            if ((!readOnly && !advisory) || !HasLexicallyEnclosingTransaction(call)) return true;
        }
        return false;
    }

    private static bool HasLexicallyEnclosingTransaction(InvocationExpressionSyntax call)
    {
        var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method?.Body == null) return false;
        if (call.Ancestors().TakeWhile(n => n != method).Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) return false;
        var declarations = method.Body.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Where(s => !s.UsingKeyword.IsKind(SyntaxKind.None) && s.SpanStart < call.SpanStart);
        foreach (var declaration in declarations)
        {
            var variable = declaration.Declaration.Variables.SingleOrDefault();
            if (variable?.Initializer?.Value is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax begin } ||
                CallName(begin) != "BeginTransactionAsync" || begin.Expression is not MemberAccessExpressionSyntax beginMember ||
                call.Expression is not MemberAccessExpressionSyntax executeMember || beginMember.Expression.ToString() != executeMember.Expression.ToString()) continue;
            var released = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c => c.SpanStart > declaration.SpanStart && c.SpanStart < call.SpanStart &&
                c.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString() == variable.Identifier.ValueText &&
                m.Name.Identifier.ValueText is "CommitAsync" or "RollbackAsync" or "DisposeAsync" or "Dispose");
            if (!released) return true;
        }
        return false;
    }
}

public sealed class SqlExecutionBoundaryTests
{
    [Theory]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY\");", false)]
    [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"SELECT pg_advisory_xact_lock({key})\");", false)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE Orders SET Total=0\");", true)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY; DELETE FROM Orders\");", true)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(sql);", true)]
    [InlineData("await db.Orders.ExecuteDeleteAsync();", true)]
    [InlineData("await db.Orders.ExecuteUpdateAsync(setters);", true)]
    [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"SELECT pg_advisory_xact_lock({key}); UPDATE Orders SET Total=0\");", true)]
    public void ClassifiesActualOperationInsideTransactionWithoutFilenameApproval(string operation, bool governed)
    {
        Assert.Equal(governed, SqlExecutionBoundary.RequiresMutationGovernance("class C { async Task M() { await using var tx = await db.Database.BeginTransactionAsync(); " + operation + " } }"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("await using var tx = await db.Database.BeginTransactionAsync(); await tx.CommitAsync();")]
    [InlineData("await using var tx = await db.Database.BeginTransactionAsync(); await tx.DisposeAsync();")]
    public void SynchronizationWithoutLiveTransactionFailsReview(string prefix) => Assert.True(SqlExecutionBoundary.RequiresMutationGovernance(
        "class C { async Task M() { " + prefix + " await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY\"); } }"));

    [Fact]
    public void CommentsAndMarkerStringsCannotAuthorizeAnActualWrite()
    {
        Assert.False(SqlExecutionBoundary.RequiresMutationGovernance("class C { string marker = \".ExecuteDeleteAsync(\"; }"));
        Assert.True(SqlExecutionBoundary.RequiresMutationGovernance("class C { async Task M() { /* SET TRANSACTION READ ONLY */ await db.Orders.ExecuteDeleteAsync(); } }"));
    }

    [Theory]
    [InlineData("await using var tx = condition ? await db.Database.BeginTransactionAsync() : null; await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY\");")]
    [InlineData("await using var tx = await other.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY\");")]
    [InlineData("await using var tx = await db.Database.BeginTransactionAsync(); Func<Task> deferred = async () => await db.Database.ExecuteSqlRawAsync(\"SET TRANSACTION READ ONLY\");")]
    public void ConditionalForeignOrEscapingTransactionCannotApproveSynchronization(string body) => Assert.True(
        SqlExecutionBoundary.RequiresMutationGovernance("class C { async Task M() { " + body + " } }"));
}
