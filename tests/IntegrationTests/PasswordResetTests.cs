using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>"Forgot my password" through the real API and database; the mail queue is the capturing stand-in (no mail server).</summary>
[Collection(ApiCollection.Name)]
public class PasswordResetTests(ApiFactory factory)
{
    private const string NewPassword = "a-brand-new-passphrase-456";

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private Task<HttpResponseMessage> Forgot(string email, string? language = null) =>
        factory.CreateApiClient().PostAsJsonAsync("/api/auth/forgot-password", new { email, language });

    private Task<HttpResponseMessage> Reset(string token, string password = NewPassword, string? language = null) =>
        factory.CreateApiClient().PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = password, language });

    private Task<HttpResponseMessage> Login(string email, string password) =>
        factory.CreateApiClient().PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<string> RequestToken(string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(email)).StatusCode);
        return factory.Mail.LatestResetToken(email) ?? throw new InvalidOperationException("No reset e-mail was sent.");
    }

    private async Task Sql(string sql, params object[] parameters)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql, parameters);
    }

    [Fact]
    public async Task The_login_page_is_told_that_password_reset_is_available()
    {
        var providers = await Json(await factory.CreateApiClient().GetAsync("/api/auth/providers"));

        Assert.True(providers.GetProperty("passwordReset").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_address_gets_the_same_answer_and_no_e_mail()
    {
        var email = $"nobody-{Guid.NewGuid():N}@example.test";

        var response = await Forgot(email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(factory.Mail.To(email));
    }

    [Fact]
    public async Task A_known_address_gets_an_e_mail_with_a_link_to_the_configured_site()
    {
        var user = await TestUser.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(user.Email.ToUpperInvariant())).StatusCode);

        var mail = Assert.Single(factory.Mail.To(user.Email));
        Assert.Equal("Reset your MevaCare password", mail.Subject);
        Assert.Contains("https://app.example.test/reset-password#token=", mail.TextBody);
        Assert.Contains("30 minutes", mail.TextBody);
    }

    [Fact]
    public async Task The_e_mail_is_written_in_romanian_when_the_app_asked_for_it()
    {
        var user = await TestUser.RegisterAsync(factory);

        await Forgot(user.Email, "ro");

        Assert.Equal("Resetează-ți parola MevaCare", Assert.Single(factory.Mail.To(user.Email)).Subject);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM [identity].PasswordResetTokens WHERE TokenHash = {token}").ToListAsync();

        Assert.Equal(0, rows.Single());
    }

    [Fact]
    public async Task Resetting_sets_the_new_password_ends_every_session_and_sends_a_notice()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);

        var reset = await Reset(token);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(user.Email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(user.Email, user.Password)).StatusCode);

        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refresh.Headers.Add("Cookie", user.RefreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().SendAsync(refresh)).StatusCode);

        Assert.Contains(factory.Mail.To(user.Email), m => m.Subject == "Your MevaCare password was changed");
    }

    [Fact]
    public async Task A_link_works_only_once()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(token)).StatusCode);

        var again = await Reset(token, "yet-another-passphrase-789");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_reset_token", Code(await Json(again)));
        Assert.Equal(HttpStatusCode.OK, (await Login(user.Email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Of_two_simultaneous_uses_of_one_link_only_one_succeeds()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);

        var results = await Task.WhenAll(Reset(token, "first-passphrase-from-race-1"), Reset(token, "second-passphrase-from-race-2"));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_made_up_token_is_refused()
    {
        var response = await Reset("not-a-real-token-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_reset_token", Code(await Json(response)));
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);
        await Sql("UPDATE [identity].PasswordResetTokens SET ExpiresAtUtc = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE UserId = {0}", user.Id);

        var response = await Reset(token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_reset_token", Code(await Json(response)));
        Assert.Equal(HttpStatusCode.OK, (await Login(user.Email, user.Password)).StatusCode);
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_the_link_stays_usable()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);

        var weak = await Reset(token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var sameAsEmail = await Reset(token, user.Email);
        Assert.Equal(HttpStatusCode.BadRequest, sameAsEmail.StatusCode);
        Assert.Equal("weak_password", Code(await Json(sameAsEmail)));

        Assert.Equal(HttpStatusCode.NoContent, (await Reset(token)).StatusCode);
    }

    [Fact]
    public async Task Asking_twice_in_a_row_sends_only_one_e_mail()
    {
        var user = await TestUser.RegisterAsync(factory);

        await Forgot(user.Email);
        await Forgot(user.Email);

        Assert.Single(factory.Mail.To(user.Email));
    }

    [Fact]
    public async Task A_newer_link_cancels_the_older_one()
    {
        var user = await TestUser.RegisterAsync(factory);
        var first = await RequestToken(user.Email);
        await Sql("UPDATE [identity].PasswordResetTokens SET CreatedAtUtc = DATEADD(minute, -10, SYSUTCDATETIME()) WHERE UserId = {0}", user.Id);

        var second = await RequestToken(user.Email);

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(second)).StatusCode);
    }

    [Fact]
    public async Task Resetting_lifts_a_lockout()
    {
        var user = await TestUser.RegisterAsync(factory);
        for (var i = 0; i < 5; i++)
        {
            await Login(user.Email, "wrong-password-" + i);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(user.Email, user.Password)).StatusCode); // locked even with the right password

        var token = await RequestToken(user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Login(user.Email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task An_account_created_with_google_can_get_a_password_this_way()
    {
        var email = FakeGoogleIdTokenValidator.NewEmail();
        var google = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", new
        {
            idToken = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), email),
            acceptTerms = true,
        });
        Assert.False((await Json(google)).GetProperty("user").GetProperty("hasPassword").GetBoolean());

        var token = await RequestToken(email);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(token)).StatusCode);

        var login = await Login(email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var user = (await Json(login)).GetProperty("user");
        Assert.True(user.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("Google", user.GetProperty("linkedProviders")[0].GetString());
    }

    [Fact]
    public async Task Without_a_mail_server_nothing_is_sent_and_the_page_is_told()
    {
        var user = await TestUser.RegisterAsync(factory);
        factory.Mail.IsConfigured = false;
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Forgot(user.Email)).StatusCode);
            Assert.Empty(factory.Mail.To(user.Email));
            var providers = await Json(await factory.CreateApiClient().GetAsync("/api/auth/providers"));
            Assert.False(providers.GetProperty("passwordReset").GetBoolean());
        }
        finally
        {
            factory.Mail.IsConfigured = true;
        }
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_reset_links()
    {
        var user = await TestUser.RegisterAsync(factory);
        var token = await RequestToken(user.Email);
        var deleted = await user.Client.PostAsJsonAsync("/api/account/delete", new { password = user.Password });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(token)).StatusCode);
    }

    [Fact]
    public async Task Both_endpoints_validate_their_input()
    {
        var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token = "", newPassword = NewPassword })).StatusCode);
    }
}
