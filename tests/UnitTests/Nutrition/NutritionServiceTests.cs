using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Auditing;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using MevaCare.AI.HealthCompanion.UnitTests.Support;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.UnitTests.Nutrition;

internal sealed class StubGate : IMealProposalGate
{
    public ProposalGateResult Result { get; set; } = new(ProposalGateState.Pending, null);
    public List<(Guid Proposal, Guid Meal)> Committed { get; } = [];

    public Task<ProposalGateResult> BeginCommitAsync(Guid userId, Guid proposalId, CancellationToken ct) => Task.FromResult(Result);

    public void MarkCommitted(Guid proposalId, Guid mealId) => Committed.Add((proposalId, mealId));
}

public class MealServiceTests
{
    private static readonly DateTimeOffset Lunch = new(2026, 10, 21, 13, 30, 0, TimeSpan.FromHours(3));

    private static async Task<(AppDbContext Db, MealService Service, Food Chicken, Food Rice, StubGate Gate)> Setup(bool consent = true)
    {
        var clock = TestDb.Clock();
        var db = TestDb.Create(clock);
        var chicken = SampleFoods.ChickenBreastRoasted();
        var rice = SampleFoods.RiceCooked();
        db.AddRange(chicken, rice);
        await db.SaveChangesAsync();
        var gate = new StubGate();
        var service = new MealService(db, new StubConsentService(consent), gate, new AuditLog(db, clock), clock);
        return (db, service, chicken, rice, gate);
    }

    private static SaveMealRequest Request(Food chicken, Food rice, Guid? proposalId = null) => new(
        null, Lunch, null,
        [
            new MealItemInput(chicken.Id, "ignored", 200m, PreparationState.Raw, null),
            new MealItemInput(rice.Id, "ignored", 150m, PreparationState.Unspecified, null),
        ],
        proposalId);

    [Fact]
    public async Task The_chicken_and_rice_meal_is_saved_with_calculated_nutrient_snapshots()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var user = Guid.NewGuid();

        var result = await service.CreateAsync(user, Request(chicken, rice), default);

        Assert.True(result.Created);
        Assert.Equal(525m, result.Meal.Totals.Calories);
        Assert.Equal(66.1m, result.Meal.Totals.ProteinG);
        Assert.Equal(["Chicken, broilers or fryers, breast, meat only, cooked, roasted", "Rice, white, long-grain, regular, enriched, cooked"],
            result.Meal.Items.Select(i => i.Name));
        // the food's own state is authoritative, not the client's claim
        Assert.All(result.Meal.Items, i => Assert.Equal("Cooked", i.Preparation));
        Assert.Equal(2, await db.Set<MealItem>().CountAsync());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Time_zone_information_is_preserved_and_the_local_date_comes_from_the_users_clock()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var lateNight = new DateTimeOffset(2026, 10, 21, 0, 30, 0, TimeSpan.FromHours(3)); // 21:30 UTC on the 20th

        var meal = (await service.CreateAsync(Guid.NewGuid(), Request(chicken, rice) with { EatenAt = lateNight }, default)).Meal;

        Assert.Equal(new DateTime(2026, 10, 20, 21, 30, 0, DateTimeKind.Utc), meal.EatenAtUtc);
        Assert.Equal(new DateOnly(2026, 10, 21), meal.LocalDate);
        Assert.Equal(180, meal.UtcOffsetMinutes);
        await db.DisposeAsync();
    }

    [Theory]
    [InlineData(7, MealType.Breakfast)]
    [InlineData(13, MealType.Lunch)]
    [InlineData(19, MealType.Dinner)]
    [InlineData(23, MealType.Snack)]
    [InlineData(3, MealType.Snack)]
    public void The_meal_type_is_inferred_from_the_local_hour(int hour, MealType expected) =>
        Assert.Equal(expected, MealService.InferType(new DateTime(2026, 1, 1, hour, 0, 0)));

    [Fact]
    public async Task Custom_items_are_stored_as_estimates_and_flag_the_totals()
    {
        var (db, service, _, _, _) = await Setup();
        var request = new SaveMealRequest(MealType.Snack, Lunch, null,
            [new MealItemInput(null, "Grandma's cake", 120m, PreparationState.Unspecified, new EstimatedNutrientsInput(400m, 5m, 50m, 20m, null))], null);

        var meal = (await service.CreateAsync(Guid.NewGuid(), request, default)).Meal;

        Assert.True(meal.Items[0].IsEstimated);
        Assert.Null(meal.Items[0].FoodId);
        Assert.True(meal.Totals.IncludesEstimates);
        Assert.True(meal.Totals.IncompleteData); // fibre unknown
        await db.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_food_id_is_rejected()
    {
        var (db, service, _, _, _) = await Setup();
        var request = new SaveMealRequest(null, Lunch, null, [new MealItemInput(Guid.NewGuid(), "x", 100m, PreparationState.Unspecified, null)], null);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(Guid.NewGuid(), request, default));

        Assert.Equal("unknown_food", ex.Code);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Without_health_data_consent_nothing_is_saved()
    {
        var (db, service, chicken, rice, _) = await Setup(consent: false);

        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.CreateAsync(Guid.NewGuid(), Request(chicken, rice), default));

        Assert.Empty(db.Set<Meal>());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Creating_from_a_proposal_marks_it_committed_with_the_new_meal()
    {
        var (db, service, chicken, rice, gate) = await Setup();
        var proposal = Guid.NewGuid();

        var result = await service.CreateAsync(Guid.NewGuid(), Request(chicken, rice, proposal), default);

        Assert.Equal([(proposal, result.Meal.Id)], gate.Committed);
        Assert.Equal("Assistant", result.Meal.Origin);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Confirming_an_already_committed_proposal_returns_the_same_meal_and_creates_nothing()
    {
        var (db, service, chicken, rice, gate) = await Setup();
        var user = Guid.NewGuid();
        var proposal = Guid.NewGuid();
        var first = await service.CreateAsync(user, Request(chicken, rice, proposal), default);
        gate.Result = new ProposalGateResult(ProposalGateState.AlreadyCommitted, first.Meal.Id);

        var retry = await service.CreateAsync(user, Request(chicken, rice, proposal), default);

        Assert.False(retry.Created);
        Assert.Equal(first.Meal.Id, retry.Meal.Id);
        Assert.Single(db.Set<Meal>());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Update_recalculates_the_snapshot_and_replaces_the_items()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var user = Guid.NewGuid();
        var meal = (await service.CreateAsync(user, Request(chicken, rice), default)).Meal;
        var smaller = new SaveMealRequest(null, null, "lighter", [new MealItemInput(rice.Id, "x", 100m, PreparationState.Unspecified, null)], null);

        var updated = await service.UpdateAsync(user, meal.Id, smaller, default);

        Assert.Single(updated.Items);
        Assert.Equal(130m, updated.Totals.Calories);
        Assert.Equal("lighter", updated.Notes);
        Assert.Equal(1, await db.Set<MealItem>().CountAsync());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Other_users_meals_cannot_be_read_updated_or_deleted()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var owner = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var meal = (await service.CreateAsync(owner, Request(chicken, rice), default)).Meal;

        await Assert.ThrowsAsync<NotFoundException>(() => service.LoadAsync(intruder, meal.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(intruder, meal.Id, Request(chicken, rice), default));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(intruder, meal.Id, default));

        Assert.Single(db.Set<Meal>());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Deleting_a_meal_removes_it_and_its_items()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var user = Guid.NewGuid();
        var meal = (await service.CreateAsync(user, Request(chicken, rice), default)).Meal;

        await service.DeleteAsync(user, meal.Id, default);

        Assert.Empty(db.Set<Meal>());
        Assert.Empty(db.Set<MealItem>());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Snapshots_do_not_change_when_the_food_data_changes_later()
    {
        var (db, service, chicken, rice, _) = await Setup();
        var user = Guid.NewGuid();
        var meal = (await service.CreateAsync(user, Request(chicken, rice), default)).Meal;

        var tracked = await db.Set<Food>().Include(f => f.Nutrients).SingleAsync(f => f.Id == chicken.Id);
        db.Remove(tracked.Nutrients.Single(n => n.Type == NutrientType.Calories));
        await db.SaveChangesAsync();

        Assert.Equal(525m, (await service.LoadAsync(user, meal.Id, default)).Totals.Calories);
        await db.DisposeAsync();
    }
}

public class NutritionSummaryServiceTests
{
    private static async Task<(AppDbContext Db, NutritionSummaryService Summary, NutritionTargetService Targets, MealService Meals, Food Chicken, Food Rice)> Setup()
    {
        var clock = TestDb.Clock();
        var db = TestDb.Create(clock);
        var chicken = SampleFoods.ChickenBreastRoasted();
        var rice = SampleFoods.RiceCooked();
        db.AddRange(chicken, rice);
        await db.SaveChangesAsync();
        var consent = new StubConsentService(true);
        var audit = new AuditLog(db, clock);
        var targets = new NutritionTargetService(db, consent, audit);
        return (db, new NutritionSummaryService(db, consent, targets), targets, new MealService(db, consent, new StubGate(), audit, clock), chicken, rice);
    }

    private static Task<SaveMealResult> Log(MealService meals, Guid user, Food food, decimal grams, DateTimeOffset at) =>
        meals.CreateAsync(user, new SaveMealRequest(null, at, null, [new MealItemInput(food.Id, "x", grams, PreparationState.Unspecified, null)], null), default);

    [Fact]
    public async Task The_daily_summary_adds_the_days_meals_and_shows_what_remains_of_the_targets()
    {
        var (db, summary, targets, meals, chicken, rice) = await Setup();
        var user = Guid.NewGuid();
        await targets.SetAsync(user, new NutritionTargetDto(2000m, 120m, null, null, null), default);
        var day = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        await Log(meals, user, chicken, 200m, day);
        await Log(meals, user, rice, 150m, day.AddHours(1));
        await Log(meals, user, rice, 999m, day.AddDays(1)); // another day

        var result = await summary.GetDailyAsync(user, new DateOnly(2026, 10, 21), default);

        Assert.Equal(525m, result.Totals.Calories);
        Assert.Equal(1475m, result.Remaining.Calories);
        Assert.Equal(53.9m, result.Remaining.ProteinG);
        Assert.Null(result.Remaining.FatG);
        Assert.Equal(2, result.Meals.Count);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task A_day_without_meals_is_an_empty_summary_not_an_error()
    {
        var (db, summary, _, _, _, _) = await Setup();

        var result = await summary.GetDailyAsync(Guid.NewGuid(), new DateOnly(2026, 1, 1), default);

        Assert.Empty(result.Meals);
        Assert.Equal(0m, result.Totals.Calories);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Other_users_meals_never_appear_in_a_summary()
    {
        var (db, summary, _, meals, chicken, _) = await Setup();
        var alice = Guid.NewGuid();
        await Log(meals, alice, chicken, 200m, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

        var bobs = await summary.GetDailyAsync(Guid.NewGuid(), new DateOnly(2026, 10, 21), default);

        Assert.Empty(bobs.Meals);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task The_range_average_only_counts_days_with_logged_meals()
    {
        var (db, summary, _, meals, chicken, _) = await Setup();
        var user = Guid.NewGuid();
        await Log(meals, user, chicken, 100m, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)); // 165
        await Log(meals, user, chicken, 200m, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)); // 330

        var range = await summary.GetRangeAsync(user, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7), default);

        Assert.Equal(2, range.DaysWithData);
        Assert.Equal(247.5m, range.AveragePerLoggedDay.Calories); // not 495/7
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_or_oversized_ranges_are_rejected()
    {
        var (db, summary, _, _, _, _) = await Setup();
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<BadRequestException>(() => summary.GetRangeAsync(user, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), default));
        await Assert.ThrowsAsync<BadRequestException>(() => summary.GetRangeAsync(user, new DateOnly(2024, 1, 1), new DateOnly(2026, 1, 1), default));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Targets_are_per_user_and_can_be_cleared()
    {
        var (db, _, targets, _, _, _) = await Setup();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await targets.SetAsync(alice, new NutritionTargetDto(2200m, 130m, 250m, 70m, 30m), default);

        Assert.Equal(2200m, (await targets.GetAsync(alice, default)).Calories);
        Assert.Null((await targets.GetAsync(bob, default)).Calories);

        await targets.SetAsync(alice, new NutritionTargetDto(null, null, null, null, null), default);
        Assert.Null((await targets.GetAsync(alice, default)).Calories);
        Assert.Single(db.Set<NutritionTarget>());
        await db.DisposeAsync();
    }
}

public class NutritionValidatorTests
{
    private readonly SaveMealRequestValidator _validator = new(TestDb.Clock());

    private static MealItemInput Item(Guid? food = null, decimal grams = 100m, EstimatedNutrientsInput? estimated = null) =>
        new(food, "x", grams, PreparationState.Unspecified, estimated);

    private SaveMealRequest Valid() => new(null, null, null, [Item(Guid.NewGuid())], null);

    [Fact]
    public void A_database_food_with_a_weight_is_valid() => Assert.True(_validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Weights_outside_zero_and_ten_thousand_grams_are_rejected(double grams) =>
        Assert.False(_validator.Validate(Valid() with { Items = [Item(Guid.NewGuid(), (decimal)grams)] }).IsValid);

    [Fact]
    public void Client_supplied_nutrients_for_database_foods_are_rejected() =>
        Assert.False(_validator.Validate(Valid() with { Items = [Item(Guid.NewGuid(), 100m, new EstimatedNutrientsInput(1, 1, 1, 1, 1))] }).IsValid);

    [Fact]
    public void Custom_items_need_at_least_one_nutrient_value()
    {
        Assert.False(_validator.Validate(Valid() with { Items = [Item(null, 100m, null)] }).IsValid);
        Assert.False(_validator.Validate(Valid() with { Items = [Item(null, 100m, new EstimatedNutrientsInput(null, null, null, null, null))] }).IsValid);
        Assert.True(_validator.Validate(Valid() with { Items = [Item(null, 100m, new EstimatedNutrientsInput(250, null, null, null, null))] }).IsValid);
    }

    [Fact]
    public void Negative_estimates_are_rejected() =>
        Assert.False(_validator.Validate(Valid() with { Items = [Item(null, 100m, new EstimatedNutrientsInput(-5, null, null, null, null))] }).IsValid);

    [Fact]
    public void A_meal_needs_items_but_not_too_many()
    {
        Assert.False(_validator.Validate(Valid() with { Items = [] }).IsValid);
        Assert.False(_validator.Validate(Valid() with { Items = Enumerable.Range(0, 31).Select(_ => Item(Guid.NewGuid())).ToList() }).IsValid);
    }

    [Fact]
    public void The_time_must_be_plausible()
    {
        var now = TestDb.Clock().GetUtcNow();
        Assert.False(_validator.Validate(Valid() with { EatenAt = now.AddDays(3) }).IsValid);
        Assert.False(_validator.Validate(Valid() with { EatenAt = now.AddYears(-10) }).IsValid);
        Assert.True(_validator.Validate(Valid() with { EatenAt = now.AddHours(-2) }).IsValid);
    }

    [Fact]
    public void Target_values_outside_sane_bounds_are_rejected()
    {
        var validator = new NutritionTargetValidator();

        Assert.False(validator.Validate(new NutritionTargetDto(-1m, null, null, null, null)).IsValid);
        Assert.False(validator.Validate(new NutritionTargetDto(25_000m, null, null, null, null)).IsValid);
        Assert.False(validator.Validate(new NutritionTargetDto(null, 5_000m, null, null, null)).IsValid);
        Assert.False(validator.Validate(new NutritionTargetDto(null, null, null, null, 400m)).IsValid);
        Assert.True(validator.Validate(new NutritionTargetDto(2000m, 120m, 250m, 70m, 30m)).IsValid);
        Assert.True(validator.Validate(new NutritionTargetDto(null, null, null, null, null)).IsValid);
    }
}

public class FoodCatalogTests
{
    [Fact]
    public async Task Search_finds_whole_words_ranks_and_flags_assumed_preparation()
    {
        await using var db = TestDb.Create();
        var cooked = SampleFoods.RiceCooked();
        var raw = SampleFoods.RiceRaw();
        db.AddRange(cooked, raw, new Food(FoodSource.UsdaSrLegacy, "1", "Candies, licorice", "Sweets", PreparationState.Unspecified));
        await db.SaveChangesAsync();
        var catalog = new LocalFoodCatalog(db);

        var unspecified = await catalog.SearchAsync(new FoodQuery("rice"), default);
        var rawWanted = await catalog.SearchAsync(new FoodQuery("raw rice"), default);

        Assert.Equal(2, unspecified.Matches.Count);          // licorice is not a match
        Assert.Equal(cooked.Id, unspecified.Matches[0].Food.Id); // cereal grains lean cooked when unspecified
        Assert.True(unspecified.PreparationAssumed);
        Assert.Equal(raw.Id, rawWanted.Matches[0].Food.Id);
        Assert.False(rawWanted.PreparationAssumed);
    }

    [Fact]
    public async Task Nothing_is_invented_when_no_food_matches()
    {
        await using var db = TestDb.Create();
        db.Add(SampleFoods.RiceCooked());
        await db.SaveChangesAsync();

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("dragon steak"), default);

        Assert.Empty(result.Matches);
    }
}
