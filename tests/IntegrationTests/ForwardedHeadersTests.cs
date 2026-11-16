using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>A host with a tight sign-in rate limit, to see whose requests are counted together.</summary>
public sealed class StrictRateLimitFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "3");
    }
}

/// <summary>
/// Behind a reverse proxy the API must rate-limit the visitor, not the proxy: the proxy (here: the test client, whose connection is on a
/// trusted loopback address) reports the visitor in X-Forwarded-For.
/// </summary>
public class ForwardedHeadersTests(StrictRateLimitFactory factory) : IClassFixture<StrictRateLimitFactory>
{
    private static Task<HttpResponseMessage> TryLogin(HttpClient client, string visitor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@example.test", password = "synthetic-passphrase-123" }),
        };
        request.Headers.Add("X-Forwarded-For", visitor);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Each_visitor_behind_the_proxy_has_a_rate_limit_of_their_own()
    {
        var client = factory.CreateApiClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(client, "203.0.113.10")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin(client, "203.0.113.10")).StatusCode);

        // Another visitor behind the same proxy is not affected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(client, "203.0.113.11")).StatusCode);
    }

    [Fact]
    public async Task A_chain_of_proxies_still_identifies_the_visitor_at_its_start()
    {
        var client = factory.CreateApiClient();

        // visitor, then a second proxy inside the private network (both proxies append to the header)
        for (var i = 0; i < 3; i++)
        {
            await TryLogin(client, "198.51.100.7, 10.0.0.5");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryLogin(client, "198.51.100.7, 172.18.0.4")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(client, "198.51.100.8, 10.0.0.5")).StatusCode);
    }
}
