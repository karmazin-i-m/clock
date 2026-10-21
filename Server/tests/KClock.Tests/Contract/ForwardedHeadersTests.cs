using System.Net;
using KClock.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// X-Forwarded-Proto is observed through Google's redirect_uri, which is built from
/// Request.Scheme: "https" only if the header was honoured. The header must count only from an
/// address in ReverseProxy:TrustedProxies — from anyone else it would let a client that reaches
/// the port directly choose its own scheme and client address.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ForwardedHeadersTests(PostgresFixture fixture) : IDisposable
{
    private readonly AppFactory _factory = new(fixture);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1", "https")]
    [InlineData("10.0.0.0/24", "10.0.0.7", "https")]
    [InlineData("192.168.1.1, 10.0.0.1", "10.0.0.1", "https")]
    [InlineData("10.0.0.1", "10.0.0.2", "http")]
    [InlineData("10.0.0.0/24", "10.0.1.7", "http")]
    [InlineData(null, "10.0.0.1", "http")]
    public async Task ForwardedProto_CountsOnlyFromATrustedProxy(string? trusted, string remote, string expectedScheme)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            if (trusted is not null)
            {
                builder.UseSetting("ReverseProxy:TrustedProxies", trusted);
            }

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse(remote))));
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/google/start");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var redirectUri = new Uri(QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"].ToString());
        Assert.Equal(expectedScheme, redirectUri.Scheme);
    }

    /// <summary>TestServer has no socket, so the peer address is whatever this sets.</summary>
    private sealed class RemoteAddressFilter(IPAddress remote) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remote;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
