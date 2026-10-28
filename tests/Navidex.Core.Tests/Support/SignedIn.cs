using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Navidex.Client;

namespace Navidex.Core.Tests.Support;

public static class SignedIn
{
    public const string Username = "admin";
    public const string Password = "correct horse battery";

    /// <summary>
    /// A client signed in as the admin, as the web app would be: the first one makes the login, the rest sign in with
    /// it. The client keeps the session cookie.
    /// </summary>
    public static HttpClient CreateSignedInClient<T>(this WebApplicationFactory<T> factory) where T : class
    {
        var http = factory.CreateClient();
        using var setup = http.PostAsJsonAsync("api/v1/auth/setup", new SetUpLogin(Username, Password)).GetAwaiter().GetResult();
        if (setup.StatusCode == HttpStatusCode.Conflict)
        {
            using var login = http.PostAsJsonAsync("api/v1/auth/login", new SignIn(Username, Password)).GetAwaiter().GetResult();
            login.EnsureSuccessStatusCode();
        }
        else
            setup.EnsureSuccessStatusCode();
        return http;
    }
}
