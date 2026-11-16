using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>Sign-in with Google through the real API and database; Google's token verification is the scripted stand-in (see <see cref="FakeGoogleIdTokenValidator"/>).</summary>
[Collection(ApiCollection.Name)]
public class GoogleSignInTests(ApiFactory factory)
{
    private static object SignIn(string token, bool acceptTerms = false, bool health = false, bool ai = false) =>
        new { idToken = token, acceptTerms, healthDataConsent = health, aiProcessingConsent = ai };

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static HttpClient Authorized(ApiFactory factory, string accessToken)
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    [Fact]
    public async Task Providers_lists_google_with_the_public_client_id_and_needs_no_sign_in()
    {
        var response = await factory.CreateApiClient().GetAsync("/api/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test-client.apps.googleusercontent.com", (await Json(response)).GetProperty("google").GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task A_new_google_identity_must_accept_the_terms_before_an_account_is_created()
    {
        var client = factory.CreateApiClient();
        var email = FakeGoogleIdTokenValidator.NewEmail();
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), email);

        var refused = await client.PostAsJsonAsync("/api/auth/google", SignIn(token));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("registration_required", Code(await Json(refused)));

        // Nothing was created: the same e-mail address can still register with a password.
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "synthetic-passphrase-123", displayName = "Same Person", acceptTerms = true });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    [Fact]
    public async Task Accepting_the_terms_creates_a_password_less_account_with_a_working_session()
    {
        var client = factory.CreateApiClient();
        var email = FakeGoogleIdTokenValidator.NewEmail();
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), email, name: "Ana Pop");

        var response = await client.PostAsJsonAsync("/api/auth/google", SignIn(token, acceptTerms: true, health: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var user = body.GetProperty("user");
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("Ana Pop", user.GetProperty("displayName").GetString());
        Assert.False(user.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(["Google"], user.GetProperty("linkedProviders").EnumerateArray().Select(p => p.GetString()!).ToArray());
        var refresh = TestUser.ExtractRefreshCookie(response);
        Assert.NotNull(refresh);

        var signedIn = Authorized(factory, body.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/auth/me")).StatusCode);

        var consents = (await Json(await signedIn.GetAsync("/api/consents"))).EnumerateArray()
            .ToDictionary(c => c.GetProperty("type").GetString()!, c => c.GetProperty("granted").GetBoolean());
        Assert.True(consents["TermsOfService"]);
        Assert.True(consents["HealthDataProcessing"]);
        Assert.False(consents["AiProcessing"]);

        // The refresh cookie works like the one of a password sign-in.
        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refreshRequest.Headers.Add("Cookie", refresh);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SendAsync(refreshRequest)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_again_returns_the_same_account_even_after_the_email_changed_at_google()
    {
        var client = factory.CreateApiClient();
        var subject = FakeGoogleIdTokenValidator.NewSubject();
        var first = await client.PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(subject, FakeGoogleIdTokenValidator.NewEmail()), acceptTerms: true));
        var id = (await Json(first)).GetProperty("user").GetProperty("id").GetGuid();

        var second = await client.PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(subject, FakeGoogleIdTokenValidator.NewEmail())));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(id, (await Json(second)).GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_existing_password_account_is_not_taken_over_by_a_matching_google_email()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), owner.Email.ToUpperInvariant());

        var response = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", SignIn(token, acceptTerms: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("google_account_not_linked", Code(await Json(response)));
        Assert.Null(TestUser.ExtractRefreshCookie(response));
        var me = await Json(await owner.Client.GetAsync("/api/auth/me"));
        Assert.Empty(me.GetProperty("linkedProviders").EnumerateArray());
        Assert.True(me.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task A_signed_in_user_can_connect_google_and_then_sign_in_with_it()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), owner.Email);

        var link = await owner.Client.PostAsJsonAsync("/api/auth/google/link", new { idToken = token });
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        var me = await Json(await owner.Client.GetAsync("/api/auth/me"));
        Assert.Equal(["Google"], me.GetProperty("linkedProviders").EnumerateArray().Select(p => p.GetString()!).ToArray());

        var signIn = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", SignIn(token));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(owner.Id, (await Json(signIn)).GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Connecting_needs_a_signed_in_user_and_a_valid_token()
    {
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), FakeGoogleIdTokenValidator.NewEmail());
        var anonymous = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google/link", new { idToken = token });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var owner = await TestUser.RegisterAsync(factory);
        var invalid = await owner.Client.PostAsJsonAsync("/api/auth/google/link", new { idToken = "not-a-google-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task One_google_identity_cannot_be_connected_twice_or_to_two_accounts()
    {
        var first = await TestUser.RegisterAsync(factory);
        var second = await TestUser.RegisterAsync(factory);
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), first.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await first.Client.PostAsJsonAsync("/api/auth/google/link", new { idToken = token })).StatusCode);

        var again = await first.Client.PostAsJsonAsync("/api/auth/google/link",
            new { idToken = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), first.Email) });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("google_already_linked", Code(await Json(again)));

        var taken = await second.Client.PostAsJsonAsync("/api/auth/google/link", new { idToken = token });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("google_in_use", Code(await Json(taken)));
    }

    [Fact]
    public async Task Google_can_be_disconnected_only_while_the_account_has_a_password()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var token = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), owner.Email);
        await owner.Client.PostAsJsonAsync("/api/auth/google/link", new { idToken = token });

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync("/api/auth/google")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.DeleteAsync("/api/auth/google")).StatusCode);

        // With the link gone the e-mail match is again refused (not silently re-linked).
        var again = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", SignIn(token, acceptTerms: true));
        Assert.Equal("google_account_not_linked", Code(await Json(again)));

        // An account that only has Google cannot remove it: that would lock the person out.
        var googleOnly = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), FakeGoogleIdTokenValidator.NewEmail()), acceptTerms: true));
        var client = Authorized(factory, (await Json(googleOnly)).GetProperty("accessToken").GetString()!);
        var refused = await client.DeleteAsync("/api/auth/google");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("password_required", Code(await Json(refused)));
    }

    [Fact]
    public async Task Google_must_vouch_for_the_email_address_and_the_token_must_be_valid()
    {
        var client = factory.CreateApiClient();

        var unverified = await client.PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), FakeGoogleIdTokenValidator.NewEmail(), emailVerified: false), acceptTerms: true));
        Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
        Assert.Equal("google_email_not_verified", Code(await Json(unverified)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/google", SignIn("garbage", acceptTerms: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/google", SignIn("", acceptTerms: true))).StatusCode);
    }

    [Fact]
    public async Task A_google_only_account_cannot_sign_in_with_a_password_and_the_answer_is_the_usual_one()
    {
        var email = FakeGoogleIdTokenValidator.NewEmail();
        await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), email), acceptTerms: true));

        var response = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/login", new { email, password = "synthetic-passphrase-123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", Code(await Json(response)));
    }

    [Fact]
    public async Task A_google_only_account_is_deleted_after_confirming_with_its_own_google_identity()
    {
        var subject = FakeGoogleIdTokenValidator.NewSubject();
        var email = FakeGoogleIdTokenValidator.NewEmail();
        var token = FakeGoogleIdTokenValidator.Token(subject, email);
        var created = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", SignIn(token, acceptTerms: true));
        var client = Authorized(factory, (await Json(created)).GetProperty("accessToken").GetString()!);

        var noProof = await client.PostAsJsonAsync("/api/account/delete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);

        var otherPerson = await client.PostAsJsonAsync("/api/account/delete",
            new { googleIdToken = FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), email) });
        Assert.Equal(HttpStatusCode.BadRequest, otherPerson.StatusCode);
        Assert.Equal("invalid_password", Code(await Json(otherPerson)));

        // A password cannot stand in for Google on an account that has no password.
        var password = await client.PostAsJsonAsync("/api/account/delete", new { password = "synthetic-passphrase-123" });
        Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var deleted = await client.PostAsJsonAsync("/api/account/delete", new { googleIdToken = token });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The Google identity is free again: signing in now asks to create a new account.
        var after = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google", SignIn(token));
        Assert.Equal("registration_required", Code(await Json(after)));
    }

    [Fact]
    public async Task A_google_identity_signs_in_to_its_own_account_only()
    {
        var a = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), FakeGoogleIdTokenValidator.NewEmail()), acceptTerms: true));
        var b = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/google",
            SignIn(FakeGoogleIdTokenValidator.Token(FakeGoogleIdTokenValidator.NewSubject(), FakeGoogleIdTokenValidator.NewEmail()), acceptTerms: true));

        Assert.NotEqual(
            (await Json(a)).GetProperty("user").GetProperty("id").GetGuid(),
            (await Json(b)).GetProperty("user").GetProperty("id").GetGuid());
    }
}
