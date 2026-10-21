using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MevaCare.AI.HealthCompanion.Api.Hosting;

/// <summary>Declares the JWT bearer scheme so the API reference can send authenticated requests.</summary>
internal sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "MevaCare AI Health Companion API";
        document.Info.Description = "Wellness information only. Not a medical device and not a substitute for professional medical advice.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        document.Security =
        [
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] },
        ];
        return Task.CompletedTask;
    }
}
