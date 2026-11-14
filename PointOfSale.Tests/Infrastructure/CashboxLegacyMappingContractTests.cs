using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Architecture;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Infrastructure;

// CLR/model metadata only: never connect, create a cashbox, post a ledger entry
// or invoke a financial service. These are not SQL02/business approval tests.
public sealed class CashboxLegacyMappingContractTests
{
    [Theory]
    [InlineData("CurrentBalance")]
    [InlineData("ExpectedBalance")]
    public void LegacyBalanceRemainsZeroWithoutExplicitControllerInitializer(string name)
    {
        var field = typeof(Cashbox).GetProperty(name);
        Assert.NotNull(field);
        Assert.Equal(typeof(decimal), field.PropertyType);
        Assert.Equal(0m, Assert.IsType<decimal>(field.GetValue(new Cashbox())));
        Assert.NotNull(Attribute.GetCustomAttribute(field, typeof(ObsoleteAttribute)));
        var root = CSharpSyntaxTree.ParseText(CertificationSource.ReadRepoFile(
            "PointOfSale.API/Controllers/CashboxController.cs")).GetRoot();
        var creation = Assert.Single(root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            node => node.Type.ToString() == "Cashbox");
        Assert.NotNull(creation.Initializer);
        Assert.DoesNotContain(creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>(),
            expression => expression.Left.ToString() == name);
        Assert.DoesNotContain(root.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            expression => expression.Name.Identifier.ValueText == name);
    }

    [Theory]
    [InlineData("CurrentBalance")]
    [InlineData("ExpectedBalance")]
    public void FullNpgsqlModelRetainsRealDecimalColumnAndPrecision(string name)
    {
        // This connection string is never opened. Only the configured model is read.
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_opened;Username=not_used").Options,
            new TestTenantContext(Guid.NewGuid()));
        var entity = db.Model.FindEntityType(typeof(Cashbox));
        Assert.NotNull(entity);
        var property = entity.FindProperty(name);
        Assert.NotNull(property);
        Assert.False(property.IsShadowProperty());
        Assert.False(property.IsNullable);
        Assert.Equal(typeof(decimal), property.ClrType);
        Assert.Equal(typeof(Cashbox).GetProperty(name), property.PropertyInfo);
        Assert.Equal(name, property.GetColumnName());
        Assert.Equal(18, property.GetPrecision());
        Assert.Equal(2, property.GetScale());
        Assert.False(property.IsConcurrencyToken);
    }
}
