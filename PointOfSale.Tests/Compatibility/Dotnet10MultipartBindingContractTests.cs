using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace PointOfSale.Tests.Compatibility;

public sealed class Dotnet10MultipartBindingContractTests
{
    [Theory]
    [InlineData("file", true)]
    [InlineData("wrong-field", false)]
    public async Task FormFileBinder_RetainsTheExactFileFieldName(string fieldName, bool expectedBound)
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var file = new FormFile(stream, 0, stream.Length, fieldName, "fixture.png");
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=fixture";
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { file });
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(IFormFile));
        var context = DefaultModelBindingContext.CreateBindingContext(
            action, new CompositeValueProvider(), metadata, bindingInfo: null, modelName: "file");
        var binder = new FormFileModelBinder(NullLoggerFactory.Instance);

        await binder.BindModelAsync(context);

        Assert.Equal(expectedBound, context.Result.IsModelSet);
        if (expectedBound) Assert.Same(file, context.Result.Model);
    }
}
