using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Navidex.Core.Access;
using Scalar.AspNetCore;

namespace Navidex.Core.Api;

/// <summary>
/// The OpenAPI document, at <c>/openapi/v1.json</c>, and the interactive reference built from it, at <c>/api/docs</c>.
/// Each operation says the scope a key needs for it.
/// </summary>
public static class OpenApiSetup
{
    private const string KeyScheme = "ApiKey";

    public static IServiceCollection AddNavidexOpenApi(this IServiceCollection services) => services.AddOpenApi("v1", openApi =>
    {
        openApi.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Navidex API";
            document.Info.Description =
                "Find what's missing from a music library, fetch it through plugins, and listen now.\n\n"
                + $"Apps authenticate with an API key in the `{AccessService.KeyHeader}` header, made under Settings → Security. Each key has scopes: "
                + "`read`, `request`, `listen` and `admin`, which allows everything. Each operation below says the scope it needs. "
                + "The client guide (`docs/client-guide.md`) covers events, webhooks and errors.";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[KeyScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = AccessService.KeyHeader,
                Description = "An API key from Settings → Security. Where a header can't be set, such as an audio element, pass it as the apikey query parameter.",
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(KeyScheme, document)] = [] }];
            return Task.CompletedTask;
        });

        openApi.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<OpenAccess>().Any())
            {
                // No key needed.
                operation.Security = [];
                operation.Description = Append(operation.Description, "Open to anyone: no key needed.");
            }
            else
            {
                var scope = AccessEndpointExtensions.ScopeFor(metadata);
                operation.Description = Append(operation.Description, $"Needs the `{scope}` scope.");
            }
            return Task.CompletedTask;
        });
    });

    /// <summary>The interactive reference. Open to anyone, as is the document it shows: neither holds anything private.</summary>
    public static void MapNavidexApiReference(this WebApplication app)
    {
        app.MapScalarApiReference("/api/docs", options => options
                .WithTitle("Navidex API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .AddPreferredSecuritySchemes(KeyScheme)
                .AddApiKeyAuthentication(KeyScheme, _ => { })
                // Self-hosted: nothing from Scalar's own servers, and none of its cloud features.
                .DisableDefaultFonts()
                .DisableTelemetry()
                .DisableAgent()
                .DisableMcp()
                .HideClientButton()
                .HideDeveloperTools())
            .AllowAnyone();
    }

    private static string Append(string? description, string line) =>
        string.IsNullOrEmpty(description) ? line : $"{description}\n\n{line}";
}
