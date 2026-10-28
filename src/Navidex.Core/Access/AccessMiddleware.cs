using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Navidex.Core.Access;

public static class AccessMiddleware
{
    public const string SessionCookie = "navidex.session";

    /// <summary>The admin's session: a cookie the web app keeps, valid until signed out or the password changes.</summary>
    public static IServiceCollection AddNavidexAccess(this IServiceCollection services)
    {
        services.AddSingleton<AccessService>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = SessionCookie;
                cookie.Cookie.HttpOnly = true;
                // Only this site sends it, so other sites can't act as the admin.
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
                cookie.SlidingExpiration = true;
                // An API answers, rather than sending people to a login page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                // A session from before the password changed, or before the login was reset, is over.
                cookie.Events.OnValidatePrincipal = async context =>
                {
                    var login = await context.HttpContext.RequestServices.GetRequiredService<AccessService>().GetLoginAsync(context.HttpContext.RequestAborted);
                    if (login is null || context.Principal?.FindFirst(AccessService.StampClaim)?.Value != login.Stamp)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        return services;
    }

    /// <summary>
    /// Checks every API call: who's calling, and whether they may call this endpoint. The web app's own files and the
    /// OpenAPI document are open, as there's nothing private in them.
    /// </summary>
    public static IApplicationBuilder UseApiAccess(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!http.Request.Path.StartsWithSegments("/api") || http.GetEndpoint() is not { } endpoint
                                                             || endpoint.Metadata.GetMetadata<OpenAccess>() is not null)
        {
            await next(http);
            return;
        }

        var access = http.RequestServices.GetRequiredService<AccessService>();
        var (caller, refusal) = await access.IdentifyAsync(http, http.RequestAborted);
        if (caller is null)
        {
            var (code, detail) = refusal switch
            {
                AccessService.Refusal.SetupRequired => ("setup-required", "Navidex has no admin login yet. Open the web app to set one."),
                AccessService.Refusal.BadKey => ("invalid-key", "That API key isn't known. It may have been revoked."),
                _ => ("sign-in-required", $"Sign in, or send an API key in the {AccessService.KeyHeader} header."),
            };
            await Problem(http, StatusCodes.Status401Unauthorized, "Not signed in", detail, code);
            return;
        }

        var scope = AccessEndpointExtensions.ScopeFor(endpoint);
        if (!caller.May(scope))
        {
            await Problem(http, StatusCodes.Status403Forbidden, "Not allowed",
                $"This key can't do that: it needs the {scope} scope. Make a key with it under Settings → Security.", "scope-required", scope);
            return;
        }

        http.Items[typeof(Caller)] = caller;
        await next(http);
    });

    private static Task Problem(HttpContext http, int status, string title, string detail, string code, string? scope = null)
    {
        http.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
            http.Response.Headers.WWWAuthenticate = $"ApiKey header=\"{AccessService.KeyHeader}\"";
        var problem = new Dictionary<string, object>
        {
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = code,
        };
        if (scope is not null)
            problem["scope"] = scope;
        return http.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}
