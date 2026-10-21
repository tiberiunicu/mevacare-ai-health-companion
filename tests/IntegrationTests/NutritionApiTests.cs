using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

[Collection(ApiCollection.Name)]
public class NutritionApiTests(ApiFactory factory)
{
    private static readonly DateTimeOffset LunchTime = new(2026, 10, 21, 13, 30, 0, TimeSpan.FromHours(3));

    private async Task<(Guid Chicken, Guid Rice)> FoodIdsAsync()
    {
        await FoodSeed.EnsureAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var foods = await db.Set<Food>().AsNoTracking().ToListAsync();
        return (foods.Single(f => f.SourceFoodId == "171477").Id, foods.Single(f => f.SourceFoodId == "168878").Id);
    }

    private static object ChickenAndRice(Guid chicken, Guid rice, object? extra = null) => new
    {
        type = "Lunch",
        eatenAt = LunchTime,
        items = new object[]
        {
            new { foodId = chicken, name = "ignored", quantityGrams = 200, preparation = "Cooked" },
            new { foodId = rice, name = "ignored", quantityGrams = 150, preparation = "Cooked" },
        },
        proposalId = extra,
    };

    [Fact]
    public async Task Nutrition_endpoints_require_authentication()
    {
        var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nutrition/foods/search?q=rice")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nutrition/summary/daily?date=2026-10-21")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/nutrition/meals", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nutrition/targets")).StatusCode);
    }

    [Fact]
    public async Task Food_search_returns_ranked_foods_with_nutrients_per_100_g_and_nothing_for_unknown_foods()
    {
        await FoodSeed.EnsureAsync(factory.Services);
        var user = await TestUser.RegisterAsync(factory);

        var found = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/search?q=cooked%20rice");
        var none = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/foods/search?q=dragon%20steak");

        Assert.Equal("Rice, white, long-grain, regular, enriched, cooked", found[0].GetProperty("description").GetString());
        Assert.Equal(130m, found[0].GetProperty("per100g").GetProperty("calories").GetDecimal());
        Assert.Equal("UsdaSrLegacy #168878", found[0].GetProperty("sourceLabel").GetString());
        Assert.Equal(0, none.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.GetAsync("/api/nutrition/foods/search?q=a")).StatusCode);
    }

    [Fact]
    public async Task Logging_chicken_and_rice_updates_the_journal_and_the_daily_summary()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);
        (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 2000, proteinG = 120 })).EnsureSuccessStatusCode();

        var created = await user.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var meal = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(525m, meal.GetProperty("totals").GetProperty("calories").GetDecimal());
        Assert.Equal("2026-10-21", meal.GetProperty("localDate").GetString());
        Assert.Equal(180, meal.GetProperty("utcOffsetMinutes").GetInt32());

        var summary = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21");
        Assert.Equal(525m, summary.GetProperty("totals").GetProperty("calories").GetDecimal());
        Assert.Equal(66.1m, summary.GetProperty("totals").GetProperty("proteinG").GetDecimal());
        Assert.Equal(1475m, summary.GetProperty("remaining").GetProperty("calories").GetDecimal());
        Assert.Equal(1, summary.GetProperty("meals").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("remaining").GetProperty("fatG").ValueKind);

        var otherDay = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-22");
        Assert.Equal(0m, otherDay.GetProperty("totals").GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task Nutrient_values_supplied_by_the_client_for_a_database_food_are_refused()
    {
        var (chicken, _) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            items = new[] { new { foodId = chicken, name = "x", quantityGrams = 100, preparation = "Cooked", estimated = new { calories = 1 } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Custom_items_are_saved_as_estimates_and_marked_in_the_totals()
    {
        var user = await TestUser.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            eatenAt = LunchTime,
            items = new[] { new { name = "Home-made cake", quantityGrams = 120, preparation = "Unspecified", estimated = new { calories = 400, proteinG = 5 } } },
        });

        response.EnsureSuccessStatusCode();
        var meal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(meal.GetProperty("items")[0].GetProperty("isEstimated").GetBoolean());
        Assert.True(meal.GetProperty("totals").GetProperty("includesEstimates").GetBoolean());
        Assert.True(meal.GetProperty("totals").GetProperty("incompleteData").GetBoolean());
    }

    [Fact]
    public async Task Meals_can_be_edited_and_deleted_and_the_summary_follows()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);
        var meal = await (await user.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice))).Content.ReadFromJsonAsync<JsonElement>();
        var id = meal.GetProperty("id").GetGuid();

        var edit = await user.Client.PutAsJsonAsync($"/api/nutrition/meals/{id}", new
        {
            eatenAt = LunchTime,
            notes = "smaller rice",
            items = new object[]
            {
                new { foodId = chicken, name = "x", quantityGrams = 200, preparation = "Cooked" },
                new { foodId = rice, name = "x", quantityGrams = 50, preparation = "Cooked" },
            },
        });
        edit.EnsureSuccessStatusCode();
        var afterEdit = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21");
        Assert.Equal(395m, afterEdit.GetProperty("totals").GetProperty("calories").GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync($"/api/nutrition/meals/{id}")).StatusCode);
        var afterDelete = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21");
        Assert.Equal(0, afterDelete.GetProperty("meals").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/nutrition/meals/{id}")).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_see_edit_or_delete_each_others_meals_or_targets()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var alice = await TestUser.RegisterAsync(factory);
        var bob = await TestUser.RegisterAsync(factory);
        (await alice.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 1800 })).EnsureSuccessStatusCode();
        var meal = await (await alice.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice))).Content.ReadFromJsonAsync<JsonElement>();
        var id = meal.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"/api/nutrition/meals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.PutAsJsonAsync($"/api/nutrition/meals/{id}", ChickenAndRice(chicken, rice))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"/api/nutrition/meals/{id}")).StatusCode);
        var bobsDay = await bob.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21");
        Assert.Equal(0, bobsDay.GetProperty("meals").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await bob.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/targets")).GetProperty("calories").ValueKind);
        Assert.Equal(1, (await alice.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21")).GetProperty("meals").GetArrayLength());
    }

    [Fact]
    public async Task Nutrition_data_needs_the_health_data_consent()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory, healthConsent: false);

        var response = await user.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("consent_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/nutrition/summary/daily?date=2026-10-21")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/nutrition/targets")).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_by_validation_and_by_database_constraints()
    {
        var (chicken, _) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/nutrition/meals", new { items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/nutrition/meals",
            new { items = new[] { new { foodId = chicken, name = "x", quantityGrams = -5, preparation = "Cooked" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/nutrition/meals",
            new { items = new[] { new { foodId = Guid.NewGuid(), name = "x", quantityGrams = 100, preparation = "Cooked" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.GetAsync("/api/nutrition/summary/range?from=2026-02-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.GetAsync("/api/nutrition/summary/range?from=2020-01-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task The_range_summary_lists_logged_days_and_averages_only_those()
    {
        var (chicken, _) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);
        foreach (var (day, grams) in new[] { (1, 100), (3, 200) })
        {
            (await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
            {
                eatenAt = new DateTimeOffset(2026, 9, day, 12, 0, 0, TimeSpan.Zero),
                items = new[] { new { foodId = chicken, name = "x", quantityGrams = grams, preparation = "Cooked" } },
            })).EnsureSuccessStatusCode();
        }

        var range = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/range?from=2026-09-01&to=2026-09-07");

        Assert.Equal(2, range.GetProperty("daysWithData").GetInt32());
        Assert.Equal(247.5m, range.GetProperty("averagePerLoggedDay").GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task Meals_and_targets_are_exported_and_erased_with_the_account()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);
        (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 2100 })).EnsureSuccessStatusCode();
        var meal = await (await user.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice))).Content.ReadFromJsonAsync<JsonElement>();
        var mealId = meal.GetProperty("id").GetGuid();

        var export = await user.Client.GetFromJsonAsync<JsonElement>("/api/account/export");
        Assert.Equal(1, export.GetProperty("nutrition").GetProperty("meals").GetArrayLength());
        Assert.Equal(2100m, export.GetProperty("nutrition").GetProperty("targets").GetProperty("calories").GetDecimal());

        (await user.Client.PostAsJsonAsync("/api/account/delete", new { password = user.Password })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Set<Meal>().AnyAsync(m => m.Id == mealId));
        Assert.False(await db.Set<MealItem>().AnyAsync(i => i.MealId == mealId));
        Assert.False(await db.Set<NutritionTarget>().AnyAsync(t => t.UserId == user.Id));
        Assert.True(await db.Set<Food>().AnyAsync(f => f.SourceFoodId == "171477")); // shared food data stays
    }

    [Fact]
    public async Task Saved_items_keep_a_snapshot_of_the_nutrients_even_if_the_food_data_changes()
    {
        var (chicken, rice) = await FoodIdsAsync();
        var user = await TestUser.RegisterAsync(factory);
        var meal = await (await user.Client.PostAsJsonAsync("/api/nutrition/meals", ChickenAndRice(chicken, rice))).Content.ReadFromJsonAsync<JsonElement>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Set<FoodNutrient>().Where(n => n.FoodId == chicken && n.Type == NutrientType.Calories).ExecuteUpdateAsync(s => s.SetProperty(n => n.AmountPer100g, 999m));
        }

        try
        {
            var again = await user.Client.GetFromJsonAsync<JsonElement>($"/api/nutrition/meals/{meal.GetProperty("id").GetGuid()}");
            Assert.Equal(525m, again.GetProperty("totals").GetProperty("calories").GetDecimal());
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Set<FoodNutrient>().Where(n => n.FoodId == chicken && n.Type == NutrientType.Calories).ExecuteUpdateAsync(s => s.SetProperty(n => n.AmountPer100g, 165m));
        }
    }
}
