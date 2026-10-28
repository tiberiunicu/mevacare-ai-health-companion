using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>
/// The online food lookup (Open Food Facts) end to end against a SCRIPTED stand-in for the service: consent, preview vs. add, validation of the
/// provider's data, idempotency, provider failures, and that an added product can be searched and logged like any other food.
/// </summary>
[Collection(ApiCollection.Name)]
public class OnlineFoodApiTests : IDisposable
{
    private readonly ApiFactory _factory;

    public OnlineFoodApiTests(ApiFactory factory)
    {
        _factory = factory;
        factory.OpenFoodFacts.Reset();
    }

    public void Dispose() => _factory.OpenFoodFacts.Reset();

    private async Task<TestUser> UserAsync(bool externalConsent = true)
    {
        var user = await TestUser.RegisterAsync(_factory);
        if (externalConsent)
        {
            (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();
        }

        return user;
    }

    private static string Barcode() => (5_000_000_000_000L + Random.Shared.NextInt64(1_000_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_online_endpoints_require_authentication()
    {
        var client = _factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nutrition/foods/online/search?q=hochland")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = "5941238007810" })).StatusCode);
    }

    [Fact]
    public async Task Without_the_external_integrations_consent_nothing_is_sent_to_the_provider()
    {
        var user = await UserAsync(externalConsent: false);
        _factory.OpenFoodFacts.Products["5941238007810"] = FakeOpenFoodFactsHandler.Hochland();

        var search = await user.Client.GetAsync("/api/nutrition/foods/online/search?q=hochland");
        var add = await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = "5941238007810" });

        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Empty(_factory.OpenFoodFacts.Requests);
    }

    [Fact]
    public async Task Searching_online_previews_candidates_without_storing_them()
    {
        var user = await UserAsync();
        var code = Barcode();
        _factory.OpenFoodFacts.Products[code] = FakeOpenFoodFactsHandler.Hochland(code, brand: "Qelmora");
        _factory.OpenFoodFacts.Products["9999999999990"] = FakeOpenFoodFactsHandler.Hochland("9999999999990", kcal: "9999", brand: "Qelmora");

        var found = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/online/search?q=qelmora%20cottage%20cheese");
        var local = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/search?q=cottage%20cheese%20qelmora");

        var candidate = Assert.Single(found.EnumerateArray());   // the implausible product is not offered
        Assert.Equal(code, candidate.GetProperty("barcode").GetString());
        Assert.Equal("Qelmora", candidate.GetProperty("brand").GetString());
        Assert.Equal(95m, candidate.GetProperty("per100g").GetProperty("calories").GetDecimal());
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("foodId").ValueKind);
        Assert.Equal(0, local.GetArrayLength());
    }

    [Fact]
    public async Task An_added_product_is_found_by_brand_and_barcode_and_can_be_logged()
    {
        var user = await UserAsync();
        var code = Barcode();
        _factory.OpenFoodFacts.Products[code] = FakeOpenFoodFactsHandler.Hochland(code, brand: "Tavonik");

        var added = await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = code });
        added.EnsureSuccessStatusCode();
        var food = await added.Content.ReadFromJsonAsync<JsonElement>();
        var byBrand = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/search?q=tavonik%20cottage");
        var byBarcode = await user.Client.GetFromJsonAsync<JsonElement>($"/api/nutrition/foods/search?q={code}");

        Assert.Equal($"OpenFoodFacts #{code}", food.GetProperty("sourceLabel").GetString());
        Assert.Equal("Tavonik", food.GetProperty("brand").GetString());
        Assert.Equal(food.GetProperty("foodId").GetGuid(), byBrand.EnumerateArray().Select(f => f.GetProperty("foodId").GetGuid()).Single());
        Assert.Equal(code, Assert.Single(byBarcode.EnumerateArray()).GetProperty("barcode").GetString());

        var meal = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            type = "Snack",
            items = new object[] { new { foodId = food.GetProperty("foodId").GetGuid(), name = "ignored", quantityGrams = 175, preparation = "Unspecified" } },
        });
        meal.EnsureSuccessStatusCode();
        var saved = await meal.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(166.3m, saved.GetProperty("totals").GetProperty("calories").GetDecimal());   // 175 g × 95 kcal / 100 g = 166.25, shown with one decimal
    }

    [Fact]
    public async Task Adding_the_same_product_twice_reads_the_provider_once_and_returns_the_same_food()
    {
        var user = await UserAsync();
        var code = Barcode();
        _factory.OpenFoodFacts.Products[code] = FakeOpenFoodFactsHandler.Hochland(code);

        var first = await (await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = code })).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = code })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("foodId").GetGuid(), second.GetProperty("foodId").GetGuid());
        Assert.Single(_factory.OpenFoodFacts.Requests, r => r.Contains($"/api/v2/product/{code}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stored_product_is_marked_in_later_searches_so_it_can_be_used_right_away()
    {
        var user = await UserAsync();
        var code = Barcode();
        _factory.OpenFoodFacts.Products[code] = FakeOpenFoodFactsHandler.Hochland(code, brand: "Zindra");
        var added = await (await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = code })).Content.ReadFromJsonAsync<JsonElement>();

        var found = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/online/search?q=zindra");

        Assert.Equal(added.GetProperty("foodId").GetGuid(), found[0].GetProperty("foodId").GetGuid());
    }

    [Fact]
    public async Task Adding_what_the_provider_does_not_have_or_data_that_is_unusable_is_refused_cleanly()
    {
        var user = await UserAsync();
        var bad = Barcode();
        _factory.OpenFoodFacts.Products[bad] = FakeOpenFoodFactsHandler.Hochland(bad, kcal: "9999");

        var unknown = await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = "4006381333931" });
        var unusable = await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = bad });
        var malformed = await user.Client.PostAsJsonAsync("/api/nutrition/foods/online/add", new { barcode = "12345" });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unusable.StatusCode);
        Assert.Equal("product_data_unusable", (await unusable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task A_failing_provider_is_a_503_that_does_not_leak_provider_details()
    {
        var user = await UserAsync();
        _factory.OpenFoodFacts.FailWith = HttpStatusCode.InternalServerError;

        var response = await user.Client.GetAsync("/api/nutrition/foods/online/search?q=hochland");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("food_online_unavailable", body);
        Assert.DoesNotContain("provider internal detail", body);
    }

    [Fact]
    public async Task A_too_short_search_text_is_rejected_before_anything_is_sent()
    {
        var user = await UserAsync();

        var response = await user.Client.GetAsync("/api/nutrition/foods/online/search?q=a");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.OpenFoodFacts.Requests);
    }
}
