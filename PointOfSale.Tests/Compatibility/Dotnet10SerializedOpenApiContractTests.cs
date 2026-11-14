using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Infrastructure.Swagger;
using Swashbuckle.AspNetCore.Swagger;

namespace PointOfSale.Tests.Compatibility;

public sealed class Dotnet10SerializedOpenApiContractTests
{
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ApplicationName).Returns(typeof(HomepageMediaController).Assembly.GetName().Name!);
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        environment.SetupGet(e => e.ContentRootPath).Returns(AppContext.BaseDirectory);
        services.AddSingleton(environment.Object);
        services.AddSingleton<IHostEnvironment>(environment.Object);
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(HomepageMediaController).Assembly);
        services.AddEndpointsApiExplorer();
        services.AddPointOfSaleSwagger();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SerializedProductionDocument_PreservesAllCheckoutHeaderAndBearerContracts()
    {
        using var services = Services();
        var document = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        Assert.Equal(OpenApiSpecVersion.OpenApi3_0, new SwaggerOptions().OpenApiVersion);
        using var json = JsonDocument.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
        var root = json.RootElement;
        Assert.StartsWith("3.0.", root.GetProperty("openapi").GetString());
        Assert.Equal("Point of Sale API", root.GetProperty("info").GetProperty("title").GetString());
        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("Authorization", scheme.GetProperty("name").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Contains(root.GetProperty("security").EnumerateArray(), r => r.TryGetProperty("Bearer", out var scopes) && scopes.GetArrayLength() == 0);

        var checkout = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => d.ActionDescriptor is ControllerActionDescriptor action && action.ControllerTypeInfo.AsType() == typeof(CheckoutController))
            .ToList();
        Assert.NotEmpty(checkout);
        foreach (var action in checkout)
        {
            var operation = root.GetProperty("paths").GetProperty("/" + action.RelativePath!.TrimStart('/'))
                .GetProperty(action.HttpMethod!.ToLowerInvariant());
            var headers = operation.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("in").GetString() == "header").ToList();
            foreach (var (name, required, format) in new[]
            {
                ("Idempotency-Key", true, "uuid"), ("X-Request-Id", false, "uuid"),
                ("X-Tenant-Id", true, "uuid"), ("X-Session-Id", false, (string?)null), ("X-Customer-Id", false, "uuid")
            })
            {
                var header = Assert.Single(headers, p => p.GetProperty("name").GetString() == name);
                Assert.Equal(required, header.TryGetProperty("required", out var flag) && flag.GetBoolean());
                var schema = header.GetProperty("schema");
                Assert.Equal("string", schema.GetProperty("type").GetString());
                if (format is not null) Assert.Equal(format, schema.GetProperty("format").GetString());
                if (name == "Idempotency-Key")
                    Assert.Equal("3fa85f64-5717-4562-b3fc-2c963f66afa6", schema.GetProperty("example").GetString());
            }
        }
    }

    [Fact]
    public async Task SerializedProductionUpload_RetainsRequiredMultipartBinaryFile()
    {
        using var services = Services();
        var document = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var json = JsonDocument.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
        var request = json.RootElement.GetProperty("paths").GetProperty("/api/admin/homepage-media").GetProperty("post").GetProperty("requestBody");
        Console.WriteLine("SERIALIZED_UPLOAD_REQUEST_BODY=" + request.GetRawText());
        Assert.True(request.GetProperty("required").GetBoolean());
        var schema = request.GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var file = schema.GetProperty("properties").GetProperty("file");
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), p => p.GetString() == "file");
    }
}
