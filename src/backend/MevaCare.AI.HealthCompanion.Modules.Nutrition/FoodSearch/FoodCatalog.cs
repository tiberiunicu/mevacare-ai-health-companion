using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

public sealed record FoodQuery(
    string Text, PreparationState Preparation = PreparationState.Unspecified, int Limit = 5, bool IncludeNutrients = false);

public sealed record FoodMatch(Food Food, double Score);

/// <param name="Ambiguous">The best matches score almost the same; the user should check the choice.</param>
/// <param name="PreparationAssumed">The user did not say raw or cooked but the chosen food is specifically one of them.</param>
public sealed record FoodSearchResult(IReadOnlyList<FoodMatch> Matches, bool Ambiguous, bool PreparationAssumed);

/// <summary>
/// Search over a verified food database. The first implementation reads the local, imported USDA data; a Romanian
/// food table or another database is added as another implementation (or another <see cref="FoodSource"/>) behind
/// this interface without touching the callers.
/// </summary>
public interface IFoodCatalog
{
    Task<FoodSearchResult> SearchAsync(FoodQuery query, CancellationToken ct);

    /// <summary>Loads a food with its nutrients and portions.</summary>
    Task<Food?> GetAsync(Guid id, CancellationToken ct);
}

public sealed class LocalFoodCatalog(AppDbContext db) : IFoodCatalog
{
    private const int CandidateLimit = 400;
    private const double AmbiguityMargin = 2.0;

    public async Task<FoodSearchResult> SearchAsync(FoodQuery query, CancellationToken ct)
    {
        var (tokens, wanted) = FoodRanking.SplitPreparation(query.Text, query.Preparation);
        if (tokens.Count == 0)
        {
            return new FoodSearchResult([], false, false);
        }

        // Narrow in SQL (substring per token), then match whole words and rank in code.
        IQueryable<Food> candidates = db.Set<Food>().AsNoTracking();
        foreach (var token in tokens)
        {
            var t = token;
            var stem = SqlStem(t);
            candidates = candidates.Where(f => f.Description.ToLower().Contains(stem));
        }

        var found = await candidates.OrderBy(f => f.Description.Length).Take(CandidateLimit).ToListAsync(ct);
        var ranked = found
            .Where(f => FoodRanking.MatchesAll(f.Description, tokens))
            .Select(f => new FoodMatch(f, FoodRanking.Score(f, tokens, wanted)))
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Food.Description.Length)
            .ThenBy(m => m.Food.Description, StringComparer.Ordinal)
            .Take(Math.Clamp(query.Limit, 1, 25))
            .ToList();

        if (query.IncludeNutrients && ranked.Count > 0)
        {
            var ids = ranked.Select(m => m.Food.Id).ToList();
            var withNutrients = await db.Set<Food>().AsNoTracking().Include(f => f.Nutrients).Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
            ranked = ranked.Select(m => m with { Food = withNutrients[m.Food.Id] }).ToList();
        }

        var ambiguous = ranked.Count > 1 && ranked[0].Score - ranked[1].Score < AmbiguityMargin;
        var assumed = ranked.Count > 0 && wanted == PreparationState.Unspecified && ranked[0].Food.Preparation != PreparationState.Unspecified;
        return new FoodSearchResult(ranked, ambiguous, assumed);
    }

    /// <summary>"berry" must still find "blueberries" in SQL; exact word matching happens afterwards in code.</summary>
    private static string SqlStem(string token) => token.Length > 4 && token.EndsWith('y') ? token[..^1] : token;

    public Task<Food?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<Food>().AsNoTracking().Include(f => f.Nutrients).Include(f => f.Portions).SingleOrDefaultAsync(f => f.Id == id, ct);
}
