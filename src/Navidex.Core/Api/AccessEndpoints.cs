using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Core.Access;
using Navidex.Core.Data;

namespace Navidex.Core.Api;

public static class AccessEndpoints
{
    private const int MinPasswordLength = 8;

    public static RouteGroupBuilder MapAccessEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Access");

        auth.MapGet("/status", async (HttpContext http, AccessService access, CancellationToken ct) =>
            {
                var login = await access.GetLoginAsync(ct);
                var (caller, _) = await access.IdentifyAsync(http, ct);
                // An app's key lets it in, but the admin's details are only for the admin.
                var admin = caller is { Kind: not CallerKind.Key };
                return new AuthStatus(login is not null, caller?.Kind == CallerKind.Admin, caller is not null, admin ? login!.Username : null,
                    admin ? login!.SkipOnLocalNetwork : null, AccessService.IsLocal(http));
            })
            .AllowAnyone()
            .WithName("GetAuthStatus")
            .WithSummary("Whether an admin login has been made, and whether this browser is signed in or let in from the local network. Open to anyone.");

        auth.MapPost("/setup", async (SetUpLogin body, HttpContext http, AccessService access, CancellationToken ct) =>
            {
                if (await access.GetLoginAsync(ct) is not null)
                    return Results.Problem("There's already an admin login. Sign in instead.", statusCode: StatusCodes.Status409Conflict, title: "Already set up");
                if (Invalid(body.Username, body.Password) is { } invalid)
                    return invalid;
                var login = AccessService.NewLogin(body.Username, body.Password, body.SkipOnLocalNetwork);
                await access.SaveLoginAsync(login, ct);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(login));
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SetUpLogin")
            .WithSummary("Make the admin login, the first time Navidex runs, and sign in with it. Refused (409) once there is one.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/login", async (SignIn body, HttpContext http, AccessService access, CancellationToken ct) =>
            {
                var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (access.TooManyFailures(address))
                    return Results.Problem("Too many wrong passwords. Wait a few minutes, then try again.", statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many tries");
                var login = await access.GetLoginAsync(ct);
                if (login is null || !string.Equals(body.Username.Trim(), login.Username, StringComparison.OrdinalIgnoreCase)
                                  || !AccessService.CheckPassword(body.Password, login.PasswordHash))
                {
                    access.Failed(address);
                    // Slows guessing, and doesn't say which of the two was wrong.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    return Results.Problem("That username and password don't match.", statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");
                }
                access.Succeeded(address);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(login));
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SignIn")
            .WithSummary("Sign in as the admin. The session is a cookie, for browsers; apps use an API key instead.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SignOut")
            .WithSummary("End this browser's session.");

        auth.MapPut("/account", async (UpdateLogin body, HttpContext http, AccessService access, CancellationToken ct) =>
            {
                var login = (await access.GetLoginAsync(ct))!;
                if (!AccessService.CheckPassword(body.CurrentPassword, login.PasswordHash))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["That isn't the current password."] });
                var username = string.IsNullOrWhiteSpace(body.Username) ? login.Username : body.Username.Trim();
                if (Invalid(username, body.NewPassword ?? new string('x', MinPasswordLength)) is { } invalid)
                    return invalid;
                // A new password signs out every other session; this one carries on under the new stamp.
                var updated = body.NewPassword is { Length: > 0 } password
                    ? login with { Username = username, PasswordHash = AccessService.HashPassword(password), Stamp = AccessService.NewStamp() }
                    : login with { Username = username };
                await access.SaveLoginAsync(updated, ct);
                if (http.User.Identity?.IsAuthenticated == true)
                    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(updated));
                return Results.NoContent();
            })
            .WithName("UpdateLogin")
            .WithSummary("Change the admin's username or password. A new password signs out every other session.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        auth.MapPut("/settings", async (AccessSettings body, AccessService access, CancellationToken ct) =>
            {
                var login = (await access.GetLoginAsync(ct))!;
                await access.SaveLoginAsync(login with { SkipOnLocalNetwork = body.SkipOnLocalNetwork }, ct);
                return Results.NoContent();
            })
            .WithName("UpdateAccessSettings")
            .WithSummary("Whether the local network is let in without signing in.")
            .Produces(StatusCodes.Status204NoContent);

        var keys = api.MapGroup("/keys").WithTags("Access");

        keys.MapGet("", async (NavidexDbContext db, CancellationToken ct) =>
                (await db.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(ct)).Select(Info).ToList())
            .RequireScope(Scopes.Admin)
            .WithName("ListApiKeys").WithSummary("The API keys apps use, without the keys themselves.");

        keys.MapPost("", async (CreateApiKey body, NavidexDbContext db, AccessService access, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(body.Name))
                    errors["name"] = ["Give the key a name, such as the app it's for."];
                if (body.Scopes.Count == 0 || body.Scopes.Any(s => !Scopes.IsKnown(s)))
                    errors["scopes"] = [$"Choose one or more of {string.Join(", ", Scopes.All)}."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                var (key, record) = AccessService.NewKey(body.Name, body.Scopes);
                db.ApiKeys.Add(record);
                await db.SaveChangesAsync(ct);
                access.KeysChanged();
                return Results.Created($"/api/v1/keys/{record.Id}", new CreatedApiKey(Info(record), key));
            })
            .WithName("CreateApiKey")
            .WithSummary("Make a key for an app. The key is in this answer only: Navidex keeps just a hash of it.")
            .Produces<CreatedApiKey>(StatusCodes.Status201Created).ProducesValidationProblem();

        keys.MapDelete("/{id:guid}", async (Guid id, NavidexDbContext db, AccessService access, CancellationToken ct) =>
            {
                var removed = await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct);
                access.KeysChanged();
                return removed == 0 ? Results.NotFound() : Results.NoContent();
            })
            .WithName("RevokeApiKey").WithSummary("Revoke a key. Apps using it are refused from then on.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        return api;
    }

    private static ApiKeyInfo Info(ApiKey key) =>
        new(key.Id, key.Name, key.Prefix, key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), key.CreatedAt, key.LastUsedAt);

    private static IResult? Invalid(string username, string password)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(username))
            errors["username"] = ["Choose a username."];
        if (password.Length < MinPasswordLength)
            errors["password"] = [$"Use at least {MinPasswordLength} characters."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
