using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.API.Controllers;

namespace PointOfSale.Tests;

public class CustomerOptionalPhoneValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalPhone_MatchesTheNullableEntityAndContactlessCustomerForm(bool update)
    {
        var state=Validate(update, "QA-ERP-20261112-CUSTOMER", null);
        Assert.True(state.IsValid, string.Join(';',state.SelectMany(pair=>pair.Value!.Errors).Select(error=>error.ErrorMessage)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredName_RemainsRequired(bool update)
    {
        Assert.False(Validate(update, null!, null).IsValid);
    }

    private static ModelStateDictionary Validate(bool update,string name,string? phone)
    {
        var services=new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider=services.BuildServiceProvider();
        var context=new ActionContext(new DefaultHttpContext{RequestServices=provider},new RouteData(),new ActionDescriptor(),new ModelStateDictionary());
        object request=update ? new UpdateCustomerRequest(name,phone,null,null,null,null) : new CreateCustomerRequest(name,phone,null,null,null,null);
        provider.GetRequiredService<IObjectModelValidator>().Validate(context,null,"",request);
        return context.ModelState;
    }
}
