using System.Net;
using KClock.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// Where the browser lands after Google is carried inside the encrypted OAuth state, so the
/// only honest way to see it is to unprotect that state with the handler's own format. Every
/// unsafe ReturnUrl must fall back rather than be passed through: the callback redirects to
/// it verbatim, which would make /auth/google/start an open redirect.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GoogleStartTests(PostgresFixture fixture) : IDisposable
{
    private readonly AppFactory _factory = new(fixture);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/me", "/api/me")]
    [InlineData("/swagger/index.html?x=1", "/swagger/index.html?x=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/clocks")]
    [InlineData("", "/clocks")]
    [InlineData("https://evil.example/", "/clocks")]
    [InlineData("//evil.example/", "/clocks")]
    [InlineData("/\\evil.example/", "/clocks")]
    [InlineData("api/me", "/clocks")]
    [InlineData("/api/me\n", "/clocks")]
    public async Task Start_CarriesOnlyLocalReturnUrlThroughGoogle(string? returnUrl, string expected)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var path = returnUrl is null
            ? "/auth/google/start"
            : QueryHelpers.AddQueryString("/auth/google/start", "ReturnUrl", returnUrl);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var google = response.Headers.Location!;
        Assert.Equal("accounts.google.com", google.Host);
        var state = QueryHelpers.ParseQuery(google.Query)["state"].ToString();

        var options = _factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(state);
        Assert.NotNull(properties);
        Assert.Equal(expected, properties.RedirectUri);
    }
}
