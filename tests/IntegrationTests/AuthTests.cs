using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    private static object RegistrationBody(string email, string password = "synthetic-passphrase-123") => new
    {
        email,
        password,
        displayName = "Synthetic User",
        acceptTerms = true,
    };

    [Fact]
    public async Task Health_endpoints_report_a_working_database()
    {
        var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/health-profile")]
    [InlineData("/api/health-goals")]
    [InlineData("/api/health-preferences")]
    [InlineData("/api/consents")]
    [InlineData("/api/account/export")]
    public async Task Protected_endpoints_reject_anonymous_callers(string url)
    {
        var response = await factory.CreateApiClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_tokens_are_rejected()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken[..^3] + "abc");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Register_then_login_then_me_returns_the_same_user()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email.ToUpperInvariant(), password = user.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await (await client.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, me.GetProperty("id").GetGuid());
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Duplicate_email_addresses_are_rejected_case_insensitively()
    {
        var user = await TestUser.RegisterAsync(factory);

        var response = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/register", RegistrationBody(user.Email.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_in_use", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Registration_cannot_assign_roles_through_mass_assignment()
    {
        var client = factory.CreateApiClient();
        var email = $"mass-{Guid.NewGuid():N}@example.test";

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "synthetic-passphrase-123",
            displayName = "Mallory",
            acceptTerms = true,
            roles = new[] { "Admin" },
            isActive = true,
            id = Guid.NewGuid(),
        });

        response.EnsureSuccessStatusCode();
        var roles = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("roles");
        Assert.Equal(["User"], roles.EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Registration_requires_accepting_the_terms_and_a_strong_password()
    {
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "a@example.test", password = "short", displayName = "A", acceptTerms = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("Password", out _));
        Assert.True(errors.TryGetProperty("AcceptTerms", out _));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_are_indistinguishable()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "definitely-wrong-password" });
        var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.test", password = "definitely-wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync().ContinueWith(t => Detail(t.Result)),
            await unknownUser.Content.ReadAsStringAsync().ContinueWith(t => Detail(t.Result)));
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures_even_with_the_right_password()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-attempt" });
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_of_an_old_token_revokes_the_whole_session()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();

        var first = await Refresh(client, user.RefreshCookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = TestUser.ExtractRefreshCookie(first)!;
        Assert.NotEqual(user.RefreshCookie, rotated);

        // Replaying the already-rotated token is treated as theft...
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, user.RefreshCookie)).StatusCode);
        // ...and kills the legitimately rotated token too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, rotated)).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = factory.CreateApiClient();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", user.RefreshCookie);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, user.RefreshCookie)).StatusCode);
    }

    [Fact]
    public async Task Refresh_cookie_is_http_only_and_scoped_to_the_auth_path()
    {
        var client = factory.CreateApiClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", RegistrationBody($"cookie-{Guid.NewGuid():N}@example.test"));

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mc_refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_responses_are_not_cacheable_and_carry_security_headers()
    {
        var user = await TestUser.RegisterAsync(factory);

        var response = await user.Client.GetAsync("/api/auth/me");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static string? Detail(string json) => JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString();
}
