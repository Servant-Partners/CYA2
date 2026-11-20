using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class ReturnUrlSecurityTests
{
    public static TheoryData<string?, bool> ReturnUrls => new()
    {
        { null, false },
        { "", false },
        { " ", false },
        { "/", true },
        { "/donors", true },
        { "/donors?filter=active", true },
        { "https://example.invalid", false },
        { "http://example.invalid", false },
        { "//example.invalid", false },
        { "/\\example.invalid", false },
        { "\\\\example.invalid", false },
        { "donors", false },
        { "/donors\r\n", false },
        { "/\texample.invalid", false }
    };

    [Theory]
    [MemberData(nameof(ReturnUrls))]
    public async Task Logout_OnlyRedirectsToLocalDestinations(string? returnUrl, bool isLocal)
    {
        using var host = new WebAuthorizationTestHost();
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Add(WebAuthorizationTestHost.IdentityHeader, "regular");

        using var response = await client.GetAsync(BuildRequestUrl("/logout", returnUrl));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(isLocal ? returnUrl : "/logged-out", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [MemberData(nameof(ReturnUrls))]
    public async Task Login_OnlyPassesLocalDestinationsToGoogleChallenge(string? returnUrl, bool isLocal)
    {
        var authentication = new RecordingAuthenticationService();
        using var root = new WebAuthorizationTestHost();
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Google:ClientId"] = "test-client-id",
                    ["Authentication:Google:ClientSecret"] = "test-client-secret"
                }));
            builder.ConfigureServices(services => services.AddSingleton<IAuthenticationService>(authentication));
        });
        using var client = CreateClient(host);

        using var response = await client.GetAsync(BuildRequestUrl("/api/login", returnUrl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GoogleDefaults.AuthenticationScheme, authentication.ChallengeScheme);
        Assert.Equal(isLocal ? returnUrl : "/", authentication.ChallengeProperties?.RedirectUri);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> host) => host.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static string BuildRequestUrl(string path, string? returnUrl) => returnUrl is null
        ? path
        : $"{path}?returnUrl={Uri.EscapeDataString(returnUrl)}";

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? ChallengeScheme { get; private set; }
        public AuthenticationProperties? ChallengeProperties { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            ChallengeScheme = scheme;
            ChallengeProperties = properties;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, System.Security.Claims.ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }
}
