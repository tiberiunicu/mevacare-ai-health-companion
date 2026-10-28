using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;

/// <summary>A product found online. It is a preview: nothing is stored until the user adds it (<see cref="OnlineFoodService.AddAsync"/>).</summary>
/// <param name="FoodId">Set when the product is already in the local database, so it can be used right away.</param>
public sealed record OnlineFoodCandidateDto(
    string Barcode, string Name, string? Brand, string? Category, string? Quantity, NutrientValues Per100g, string SourceLabel, Guid? FoodId,
    bool EnergyDerived);

/// <summary>
/// The optional, user-triggered lookup in Open Food Facts for products the local database does not have. Two steps by design: a search that only
/// previews candidates, and an explicit "add" that re-reads the product by barcode on the server (client-supplied numbers are never stored)
/// and saves it with its provenance. Needs the External integrations consent because the text searched for is sent to a third party.
/// </summary>
public sealed class OnlineFoodService(AppDbContext db, IOpenFoodFactsClient client, IConsentService consents, Microsoft.Extensions.Options.IOptions<OpenFoodFactsOptions> options)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;

    public async Task<IReadOnlyList<OnlineFoodCandidateDto>> SearchAsync(Guid userId, string text, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.ExternalIntegrations, ct);
        var query = text?.Trim() ?? "";
        if (query.Length is < MinQueryLength or > MaxQueryLength)
        {
            throw new BadRequestException("invalid_query", $"The search text must have {MinQueryLength} to {MaxQueryLength} characters.");
        }

        var products = await client.SearchAsync(query, ct);
        // The service ranks loosely ("hochland cottage cheese" also returns other cottage cheeses). Like the local search, keep only products
        // whose name or brand contains every word of the query, and only those with usable data.
        var tokens = FoodRanking.Tokenize(query);
        var foods = new List<Food>();
        foreach (var product in products)
        {
            if (OpenFoodFactsMapping.TryCreateFood(product, out var food, out _)
                && FoodRanking.MatchesAll(food!, tokens)
                && foods.All(f => f.SourceFoodId != food!.SourceFoodId))
            {
                foods.Add(food!);
            }

            if (foods.Count >= options.Value.MaxResults)
            {
                break;
            }
        }

        var barcodes = foods.Select(f => f.SourceFoodId).ToList();
        var stored = await db.Set<Food>().AsNoTracking()
            .Where(f => f.Source == FoodSource.OpenFoodFacts && barcodes.Contains(f.SourceFoodId))
            .Select(f => new { f.SourceFoodId, f.Id })
            .ToDictionaryAsync(f => f.SourceFoodId, f => f.Id, ct);

        return foods.Select(f => new OnlineFoodCandidateDto(
            f.SourceFoodId, f.Description, f.Brand, f.Category, QuantityText(f), NutrientCalculator.ForQuantity(f, 100m), f.SourceLabel,
            stored.TryGetValue(f.SourceFoodId, out var id) ? id : null, f.EnergyDerived)).ToList();
    }

    /// <summary>Stores one product, found by barcode, in the local database. Idempotent: an existing product is returned unchanged.</summary>
    public async Task<FoodSearchItemDto> AddAsync(Guid userId, string barcode, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.ExternalIntegrations, ct);
        var code = barcode?.Trim() ?? "";
        if (!FoodText.IsBarcode(code))
        {
            throw new BadRequestException("invalid_barcode", "The barcode must have 8, 12, 13 or 14 digits.");
        }

        var existing = await FindAsync(code, ct);
        if (existing is not null)
        {
            return FoodDtos.ToSearchItem(existing);
        }

        var product = await client.GetProductAsync(code, ct) ?? throw new NotFoundException("This product was not found in Open Food Facts.");
        if (!OpenFoodFactsMapping.TryCreateFood(product, out var food, out var rejection))
        {
            throw new BadRequestException("product_data_unusable", $"The product data is incomplete or implausible ({rejection}), so it was not added.");
        }

        db.Add(food!);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Added concurrently by another request (unique source + barcode): use that one.
            db.ChangeTracker.Clear();
            var concurrent = await FindAsync(code, ct);
            if (concurrent is null)
            {
                throw;
            }

            food = concurrent;
        }

        return FoodDtos.ToSearchItem(food!);
    }

    private Task<Food?> FindAsync(string code, CancellationToken ct) =>
        db.Set<Food>().AsNoTracking().Include(f => f.Nutrients).SingleOrDefaultAsync(f => f.Source == FoodSource.OpenFoodFacts && f.SourceFoodId == code, ct);

    private static string? QuantityText(Food food) =>
        food.Portions.FirstOrDefault(p => p.Unit == "package")?.Description;
}

/// <summary>Shared mapping of a food to its search-result shape.</summary>
public static class FoodDtos
{
    public static FoodSearchItemDto ToSearchItem(Food food, bool ambiguous = false, bool preparationAssumed = false) => new(
        food.Id, food.Description, food.Preparation.ToString(), food.Category, food.SourceLabel, NutrientCalculator.ForQuantity(food, 100m),
        ambiguous, preparationAssumed, food.Brand, food.Barcode, food.EnergyDerived);
}
