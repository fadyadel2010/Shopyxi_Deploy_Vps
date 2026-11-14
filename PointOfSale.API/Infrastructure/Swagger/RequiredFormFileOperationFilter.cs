using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PointOfSale.API.Infrastructure.Swagger;

/// <summary>
/// Preserve explicit required-file validation in the serialized multipart contract.
/// Swashbuckle's form request-body generation does not mark the body required.
/// </summary>
public sealed class RequiredFormFileOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var requiredFiles = context.MethodInfo.GetParameters()
            .Where(parameter => typeof(IFormFile).IsAssignableFrom(parameter.ParameterType)
                && parameter.IsDefined(typeof(RequiredAttribute), inherit: true))
            .Select(parameter => parameter.Name
                ?? throw new InvalidOperationException("Required form file has no parameter name."))
            .ToArray();
        if (requiredFiles.Length == 0) return;

        if (operation.RequestBody is not OpenApiRequestBody requestBody
            || requestBody.Content is not { } contentTypes
            || !contentTypes.TryGetValue("multipart/form-data", out var content)
            || content.Schema is not OpenApiSchema schema)
        {
            throw new InvalidOperationException("Required form file has no generated multipart schema.");
        }

        foreach (var name in requiredFiles)
        {
            if (schema.Properties?.ContainsKey(name) != true)
                throw new InvalidOperationException($"Required form file '{name}' is missing from the multipart schema.");
            schema.Required ??= new HashSet<string>();
            schema.Required.Add(name);
        }
        requestBody.Required = true;
    }
}
