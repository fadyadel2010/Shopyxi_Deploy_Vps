using Microsoft.OpenApi;

namespace PointOfSale.API.Infrastructure.Swagger;

/// <summary>
/// Shared production Swagger registration, also usable by metadata-only contract tests.
/// Registering these services does not start the application or its background workers.
/// </summary>
public static class SwaggerServiceCollectionExtensions
{
    public static IServiceCollection AddPointOfSaleSwagger(this IServiceCollection services) =>
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Point of Sale API",
                Version = "v1",
                Description = "POS System API with E-commerce integration support",
                Contact = new OpenApiContact
                {
                    Name = "API Support",
                    Email = "support@example.com"
                }
            });
            options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
            options.OperationFilter<IdempotencyKeyOperationFilter>();
            options.OperationFilter<RequiredFormFileOperationFilter>();
        });
}
