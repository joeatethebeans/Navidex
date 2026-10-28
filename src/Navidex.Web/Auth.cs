using System.Net;
using Navidex.Client;

namespace Navidex.Web;

/// <summary>
/// Whether this browser may use the app, and what its user may do. The app shows the setup or sign-in page until it's
/// signed in, and goes back to it when a call is refused, for example after the password changed.
/// </summary>
public sealed class AuthState
{
    public AuthStatus? Status { get; private set; }

    /// <summary>The status changed, or a call was refused for want of signing in.</summary>
    public event Action? Changed;

    /// <summary>Whether whoever's signed in may do this: one of <see cref="UserPermissions"/>.</summary>
    public bool May(string permission) => Status?.May(permission) == true;

    public void Set(AuthStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>A call was refused: sign in again. The setup check is left as it was.</summary>
    public void Refused()
    {
        if (Status is not { LetIn: true } status)
            return;
        Status = status with { SignedIn = false, LetIn = false };
        Changed?.Invoke();
    }
}

/// <summary>Notices the API refusing a call for want of signing in, so the app can ask.</summary>
public sealed class SignInHandler(AuthState auth) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && request.RequestUri?.AbsolutePath.Contains("/api/v1/auth/") != true)
            auth.Refused();
        return response;
    }
}
