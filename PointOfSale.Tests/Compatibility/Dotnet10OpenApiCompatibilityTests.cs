using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Infrastructure.Swagger;
using Swashbuckle.AspNetCore.Swagger;

namespace PointOfSale.Tests.Compatibility;

public sealed class Dotnet10OpenApiCompatibilityTests
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
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Compatibility fixture", Version = "v1" });
            options.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace('+', '.'));
            options.OperationFilter<IdempotencyKeyOperationFilter>();
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, Name = "Authorization", In = ParameterLocation.Header, Scheme = "Bearer"
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void HomepageUpload_RetainsMultipartFileBindingAndAuthorization()
    {
        using var services = Services();
        var descriptions = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items);
        var upload = Assert.Single(descriptions, d => d.RelativePath == "api/admin/homepage-media" && d.HttpMethod == "POST");
        var file = Assert.Single(upload.ParameterDescriptions, p => p.Name == "file");
        Assert.Equal(BindingSource.FormFile, file.Source);
        var method = typeof(HomepageMediaController).GetMethod(nameof(HomepageMediaController.Upload))!;
        var auth = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("Owner,Admin", auth.Roles);
        var limit = Assert.Single(method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true).Cast<RequestSizeLimitAttribute>());
        Assert.Equal(10 * 1024 * 1024 + 65536, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
        var document = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operations = document.Paths["/api/admin/homepage-media"].Operations
            ?? throw new InvalidOperationException("Upload operations metadata missing");
        var operation = operations[System.Net.Http.HttpMethod.Post];
        var content = operation.RequestBody?.Content
            ?? throw new InvalidOperationException("Upload request content metadata missing");
        Assert.Contains("multipart/form-data", content.Keys);
        var schema = content["multipart/form-data"].Schema!;
        Assert.Equal("binary", schema.Properties!["file"].Format);
    }

    [Fact]
    public void CheckoutFilter_RetainsRequiredHeadersAndBearerSecurity()
    {
        using var services = Services();
        var document = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var descriptions = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => d.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action &&
                action.ControllerTypeInfo.AsType() == typeof(CheckoutController));
        var checkout = descriptions.Select(d => document.Paths["/" + d.RelativePath!.TrimStart('/')]
            .Operations![new System.Net.Http.HttpMethod(d.HttpMethod!)]).ToList();
        Assert.NotEmpty(checkout);
        foreach (var operation in checkout)
        {
            foreach (var name in new[] { "Idempotency-Key", "X-Tenant-Id" })
            {
                var header = Assert.Single(operation.Parameters!, p => p.Name == name);
                Assert.True(header.Required);
                Assert.Equal(ParameterLocation.Header, header.In);
                Assert.Equal("uuid", header.Schema!.Format);
            }
        }
        Assert.Contains("Bearer", document.Components!.SecuritySchemes!.Keys);
        Assert.NotEmpty(document.Security!);
        Assert.All(document.Security!, requirement => Assert.Contains(requirement.Keys, key => key.Reference.Id == "Bearer"));
    }
}
