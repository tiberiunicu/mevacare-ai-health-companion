using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

[Collection(ApiCollection.Name)]
public class HealthProfileAndPrivacyTests(ApiFactory factory)
{
    private static readonly object ValidProfile = new
    {
        dateOfBirth = "1985-04-12",
        sex = "Female",
        heightCm = 168.5,
        activityLevel = "LightlyActive",
        timeZoneId = "Europe/Bucharest",
    };

    [Fact]
    public async Task Profile_requires_the_health_data_consent()
    {
        var user = await TestUser.RegisterAsync(factory, healthConsent: false);

        var response = await user.Client.GetAsync("/api/health-profile");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("consent_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Granting_the_consent_unlocks_the_profile_and_revoking_it_locks_it_again()
    {
        var user = await TestUser.RegisterAsync(factory, healthConsent: false);

        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/health-profile")).StatusCode);

        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/health-profile")).StatusCode);
    }

    [Fact]
    public async Task Terms_of_service_cannot_be_withdrawn_and_unknown_consents_are_rejected()
    {
        var user = await TestUser.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.Client.PutAsJsonAsync("/api/consents/TermsOfService", new { granted = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.Client.PutAsJsonAsync("/api/consents/Marketing", new { granted = true })).StatusCode);
    }

    [Fact]
    public async Task Profile_round_trips_through_the_database()
    {
        var user = await TestUser.RegisterAsync(factory);

        var empty = await user.Client.GetFromJsonAsync<JsonElement>("/api/health-profile");
        Assert.False(empty.GetProperty("exists").GetBoolean());

        (await user.Client.PutAsJsonAsync("/api/health-profile", ValidProfile)).EnsureSuccessStatusCode();
        var saved = await user.Client.GetFromJsonAsync<JsonElement>("/api/health-profile");

        Assert.True(saved.GetProperty("exists").GetBoolean());
        Assert.Equal("Female", saved.GetProperty("sex").GetString());
        Assert.Equal(168.5m, saved.GetProperty("heightCm").GetDecimal());
        Assert.Equal("1985-04-12", saved.GetProperty("dateOfBirth").GetString());
    }

    [Fact]
    public async Task Invalid_profile_values_are_rejected_with_a_validation_problem()
    {
        var user = await TestUser.RegisterAsync(factory);

        var response = await user.Client.PutAsJsonAsync("/api/health-profile", new
        {
            dateOfBirth = "2999-01-01",
            sex = "Female",
            heightCm = 5,
            activityLevel = "LightlyActive",
            timeZoneId = "Nowhere/Land",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("DateOfBirth", out _));
        Assert.True(errors.TryGetProperty("HeightCm", out _));
        Assert.True(errors.TryGetProperty("TimeZoneId", out _));
    }

    [Fact]
    public async Task Malformed_json_and_unknown_enum_values_yield_a_problem_document_not_a_server_error()
    {
        var user = await TestUser.RegisterAsync(factory);

        var response = await user.Client.PutAsync("/api/health-profile",
            new StringContent("""{"sex":"Banana","activityLevel":1}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Users_cannot_read_or_modify_each_others_data()
    {
        var alice = await TestUser.RegisterAsync(factory);
        var bob = await TestUser.RegisterAsync(factory);

        (await alice.Client.PutAsJsonAsync("/api/health-profile", ValidProfile)).EnsureSuccessStatusCode();
        var goalResponse = await alice.Client.PostAsJsonAsync("/api/health-goals", new { type = "WeightLoss", targetWeightKg = 62.5 });
        Assert.Equal(HttpStatusCode.Created, goalResponse.StatusCode);
        var goalId = (await goalResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Bob sees an empty profile and no goals...
        Assert.False((await bob.Client.GetFromJsonAsync<JsonElement>("/api/health-profile")).GetProperty("exists").GetBoolean());
        Assert.Equal(0, (await bob.Client.GetFromJsonAsync<JsonElement>("/api/health-goals")).GetArrayLength());

        // ...cannot delete Alice's goal, and it is indistinguishable from a missing goal...
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"/api/health-goals/{goalId}")).StatusCode);

        // ...and his own write never touches Alice's row.
        (await bob.Client.PutAsJsonAsync("/api/health-profile", new
        {
            sex = "Male", activityLevel = "VeryActive", timeZoneId = "UTC", heightCm = 190,
        })).EnsureSuccessStatusCode();
        var alicesProfile = await alice.Client.GetFromJsonAsync<JsonElement>("/api/health-profile");
        Assert.Equal("Female", alicesProfile.GetProperty("sex").GetString());
        Assert.Equal(1, (await alice.Client.GetFromJsonAsync<JsonElement>("/api/health-goals")).GetArrayLength());
    }

    [Fact]
    public async Task Request_bodies_cannot_override_the_owner_of_a_record()
    {
        var alice = await TestUser.RegisterAsync(factory);
        var bob = await TestUser.RegisterAsync(factory);

        var response = await bob.Client.PostAsJsonAsync("/api/health-goals", new { type = "ImproveSleep", userId = alice.Id });
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, (await alice.Client.GetFromJsonAsync<JsonElement>("/api/health-goals")).GetArrayLength());
        Assert.Equal(1, (await bob.Client.GetFromJsonAsync<JsonElement>("/api/health-goals")).GetArrayLength());
    }

    [Fact]
    public async Task Preferences_work_without_the_health_data_consent_and_reject_unsupported_languages()
    {
        var user = await TestUser.RegisterAsync(factory, healthConsent: false);

        var put = await user.Client.PutAsJsonAsync("/api/health-preferences", new { language = "ro", unitSystem = "Metric", theme = "Dark" });
        put.EnsureSuccessStatusCode();
        Assert.Equal("ro", (await user.Client.GetFromJsonAsync<JsonElement>("/api/health-preferences")).GetProperty("language").GetString());

        var bad = await user.Client.PutAsJsonAsync("/api/health-preferences", new { language = "xx", unitSystem = "Metric", theme = "Dark" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Export_contains_everything_stored_about_the_user()
    {
        var user = await TestUser.RegisterAsync(factory);
        (await user.Client.PutAsJsonAsync("/api/health-profile", ValidProfile)).EnsureSuccessStatusCode();
        (await user.Client.PostAsJsonAsync("/api/health-goals", new { type = "ImproveSleep" })).EnsureSuccessStatusCode();

        var export = await user.Client.GetFromJsonAsync<JsonElement>("/api/account/export");

        Assert.Equal(user.Email, export.GetProperty("account").GetProperty("email").GetString());
        Assert.Equal(4, export.GetProperty("consents").GetArrayLength());
        var health = export.GetProperty("healthProfile");
        Assert.Equal("Female", health.GetProperty("profile").GetProperty("sex").GetString());
        Assert.Equal(1, health.GetProperty("goals").GetArrayLength());
        Assert.DoesNotContain("passwordHash", export.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_the_account_requires_the_password_and_erases_all_data()
    {
        var user = await TestUser.RegisterAsync(factory);
        (await user.Client.PutAsJsonAsync("/api/health-profile", ValidProfile)).EnsureSuccessStatusCode();
        (await user.Client.PostAsJsonAsync("/api/health-goals", new { type = "ImproveSleep" })).EnsureSuccessStatusCode();

        var wrong = await user.Client.PostAsJsonAsync("/api/account/delete", new { password = "not-my-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/auth/me")).StatusCode);

        var deleted = await user.Client.PostAsJsonAsync("/api/account/delete", new { password = user.Password });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var login = await factory.CreateApiClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        // The still-unexpired access token no longer maps to an account.
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
    }
}
