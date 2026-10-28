using System.Text.RegularExpressions;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

/// <summary>
/// Deterministic text matching and ranking for food descriptions such as
/// "Chicken, broilers or fryers, breast, meat only, cooked, roasted". Pure functions: unit-testable without a database.
/// </summary>
public static partial class FoodRanking
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "and", "with", "in", "some", "my", "fresh", "plain",
    };

    /// <summary>Words that usually mark a product variant the user did not ask for.</summary>
    private static readonly HashSet<string> VariantWords = new(StringComparer.Ordinal)
    {
        "breaded", "battered", "canned", "baby", "babyfood", "infant", "restaurant", "fast", "imitation", "dehydrated",
        "frozen", "formula", "dietetic", "substitute", "snack", "candies", "syrup", "dried", "powder", "flour", "bran", "noodle",
        "cracker", "chip", "sausage", "roll", "sliced", "flavor", "flavored", "nugget", "patty", "spread", "mix", "juice", "peel",
        "pie", "cake", "cookie", "bar", "sauce", "soup", "oil", "paste", "extract", "concentrate", "solution", "basted", "brine",
        "nonfat", "lowfat", "loaf", "reduced", "light", "low",
    };

    /// <summary>Boilerplate words of USDA descriptions that say nothing about which product it is.</summary>
    private static readonly HashSet<string> NeutralWords = new(StringComparer.Ordinal)
    {
        "with", "without", "added", "salt", "fresh", "year", "round", "average", "all", "commercial", "varieties", "only", "meat",
        "and", "or", "of", "the", "broiler", "fryer", "skin", "skinless", "boneless", "vitamin", "enriched", "unenriched",
        "regular", "long", "grain", "not", "fortified", "includes", "food", "for", "usda", "type", "prepared",
        "drained", "solid", "as", "purchased", "unheated", "from", "in", "on", "no", "unsalted", "ripe", "commercially",
        "salted", "retail", "part", "separable", "trimmed", "cut", "choice", "select", "grade", "bone", "any", "unspecified",
    };

    private static readonly Dictionary<string, double> PreferredBonus = new(StringComparer.Ordinal)
    {
        ["whole"] = 2, ["regular"] = 1.5, ["white"] = 1, ["plain"] = 1, ["red"] = 1.5,
    };

    private static readonly HashSet<string> LowPriorityCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Baby Foods", "Fast Foods", "Restaurant Foods", "Snacks", "Sausages and Luncheon Meats", "Meals, Entrees, and Side Dishes",
        "Soups, Sauces, and Gravies", "Sweets", "Beverages", "Baked Products",
    };

    private static readonly HashSet<string> RawByDefaultCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fruits and Fruit Juices", "Vegetables and Vegetable Products", "Nut and Seed Products",
    };

    private static readonly HashSet<string> CookedByDefaultCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Poultry Products", "Beef Products", "Pork Products", "Lamb, Veal, and Game Products", "Finfish and Shellfish Products",
        "Cereal Grains and Pasta", "Legumes and Legume Products",
    };

    private static readonly HashSet<string> CookedWords = new(StringComparer.Ordinal)
    {
        "cooked", "boiled", "roasted", "baked", "fried", "grilled", "stewed", "steamed", "braised", "broiled", "microwaved",
        "simmered", "poached", "scrambled", "toasted",
    };

    private static readonly HashSet<string> RawWords = new(StringComparer.Ordinal)
    {
        "raw", "uncooked", "dry", "dried", "dehydrated",
    };

    /// <summary>Lower-cased, de-pluralised search words without stop words ("2 Eggs, boiled" → egg, boiled).</summary>
    public static IReadOnlyList<string> Tokenize(string text) =>
        Words(text).Where(w => !StopWords.Contains(w) && w.Length > 1).Select(w => Synonyms.GetValueOrDefault(w, w)).Distinct().ToList();

    /// <summary>Common names that USDA writes differently. Deliberately tiny: the assistant already translates names to generic English.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["oatmeal"] = "oat", ["porridge"] = "oat", ["yoghurt"] = "yogurt", ["aubergine"] = "eggplant", ["courgette"] = "zucchini",
    };

    /// <summary>Raw/cooked state a description refers to.</summary>
    public static PreparationState InferPreparation(string description)
    {
        var words = Words(description);
        if (words.Any(CookedWords.Contains))
        {
            return PreparationState.Cooked;
        }

        return words.Any(RawWords.Contains) ? PreparationState.Raw : PreparationState.Unspecified;
    }

    /// <summary>
    /// Removes preparation words from a search phrase and reports them ("cooked rice" → "rice", Cooked), so the state
    /// is matched by the ranking instead of being required as a word in the description.
    /// </summary>
    public static (IReadOnlyList<string> Tokens, PreparationState Preparation) SplitPreparation(string text, PreparationState hint)
    {
        var tokens = Tokenize(text);
        var preparation = hint;
        var core = new List<string>();
        foreach (var token in tokens)
        {
            if (CookedWords.Contains(token))
            {
                if (preparation == PreparationState.Unspecified)
                {
                    preparation = PreparationState.Cooked;
                }
            }
            else if (token is "raw" or "uncooked")
            {
                if (preparation == PreparationState.Unspecified)
                {
                    preparation = PreparationState.Raw;
                }
            }
            else
            {
                core.Add(token);
            }
        }

        return (core.Count > 0 ? core : tokens, preparation);
    }

    /// <summary>True when every token matches a word of the description.</summary>
    public static bool MatchesAll(string description, IReadOnlyList<string> tokens)
    {
        var words = Words(description);
        return tokens.All(t => words.Any(w => WordMatches(w, t)));
    }

    /// <summary>True when every token matches a word of the food's name or brand (a brand typed in the query counts).</summary>
    public static bool MatchesAll(Food food, IReadOnlyList<string> tokens) =>
        MatchesAll(food.Brand is null ? food.Description : food.Description + " " + food.Brand, tokens);

    /// <summary>
    /// Score of a packaged product. Names of products are free text ("Hochland Cottage cheese 175 g"), so the USDA position rules do not
    /// apply; instead: each token found in the name scores, a typed brand scores more, unrelated extra words cost a little (a plainer name is
    /// a closer match), and without a brand in the query a product ranks below generic foods, which are the better answer to "cottage cheese".
    /// </summary>
    public static double ScoreBranded(Food food, IReadOnlyList<string> tokens)
    {
        var nameWords = Words(food.Description);
        var brandWords = food.Brand is null ? [] : Words(food.Brand);
        double score = 0;
        var brandTyped = false;
        foreach (var token in tokens)
        {
            if (nameWords.Any(w => WordMatches(w, token)))
            {
                score += 6;
            }
            else if (brandWords.Any(w => WordMatches(w, token)))
            {
                score += 5;
                brandTyped = true;
            }
        }

        var extraWords = nameWords.Count(w => w.Length > 1 && !brandWords.Contains(w) && !tokens.Any(t => WordMatches(w, t)));
        score -= Math.Min(extraWords * 0.6, 6);
        // A brand written inside the product name ("Hochland Cottage cheese") also counts as typed when the query contains it.
        brandTyped |= brandWords.Count > 0 && brandWords.All(b => tokens.Any(t => WordMatches(b, t)));
        score += brandTyped ? 8 : -8;
        return Math.Round(score, 2);
    }

    /// <summary>
    /// Scores how well a food answers a query. USDA names are "base food, descriptor, descriptor, ...", so a match on the
    /// base food and early descriptors counts more, unrelated words in the first segments count against the match
    /// (they usually mean a different product), and a few clearly-processed variants and low-priority categories are
    /// pushed down unless the user asked for them. It is a transparent heuristic, not a promise: ambiguous results are
    /// flagged and alternatives are always offered.
    /// </summary>
    public static double Score(Food food, IReadOnlyList<string> tokens, PreparationState wanted)
    {
        if (food.IsBranded)
        {
            return ScoreBranded(food, tokens);
        }

        var segments = Segments(food.Description);
        double score = 0;

        foreach (var token in tokens)
        {
            var index = segments.FindIndex(segment => segment.Any(w => WordMatches(w, token)));
            score += index switch { 0 => 6, 1 => 5, 2 => 4, _ => 2.5 };
        }

        double unrelated = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            foreach (var word in segments[i])
            {
                if (word.Length < 2 || PreferredBonus.ContainsKey(word) || tokens.Any(t => WordMatches(word, t)))
                {
                    continue;
                }

                if (VariantWords.Contains(word))
                {
                    unrelated += 6; // a marker of a processed product the user did not ask for, wherever it appears
                }

                if (!IsNeutral(word))
                {
                    unrelated += i == 0 ? 3 : i == 1 ? 2.5 : 0.8;
                }
            }
        }

        score -= Math.Min(unrelated, 20);

        var allWords = segments.SelectMany(s => s).ToList();
        if (allWords.Contains("fried") && !tokens.Contains("fried"))
        {
            score -= 3;
        }

        if (food.Description.Contains("meat only", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        score += allWords.Where(PreferredBonus.ContainsKey).Distinct().Sum(w => PreferredBonus[w]);

        if (food.Category is { } category && LowPriorityCategories.Contains(category)
            && !tokens.Any(t => category.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 4;
        }

        // Raw vs cooked: an explicit request dominates; with none, categories that are normally eaten cooked lean cooked.
        var explicitWish = wanted != PreparationState.Unspecified;
        var effective = explicitWish ? wanted
            : food.Category is { } c && CookedByDefaultCategories.Contains(c) ? PreparationState.Cooked
            : food.Category is { } r && RawByDefaultCategories.Contains(r) ? PreparationState.Raw
            : PreparationState.Unspecified;
        var weight = explicitWish ? 12 : 3;
        score += effective switch
        {
            PreparationState.Cooked => food.Preparation switch { PreparationState.Cooked => weight, PreparationState.Raw => -weight, _ => 0 },
            PreparationState.Raw => food.Preparation switch { PreparationState.Raw => weight, PreparationState.Cooked => -weight, _ => 0 },
            _ => 0,
        };

        if (effective == PreparationState.Cooked && allWords.Any(w => w is "roasted" or "boiled" or "baked"))
        {
            score += 1;
        }

        return Math.Round(score, 2);
    }

    /// <summary>
    /// The words that make a food a different product (white vs brown rice, soy vs dairy yogurt), ignoring the query,
    /// raw/cooked wording and boilerplate. Two near-tied matches with the same kind are variants of one food (for example
    /// "roasted" vs "stewed" chicken breast) and are not worth warning about.
    /// </summary>
    public static bool IsAmbiguous(Food best, double bestScore, Food second, double secondScore, IReadOnlyList<string> tokens, double margin = 2.0) =>
        bestScore - secondScore < margin && !KindOf(best, tokens).SetEquals(KindOf(second, tokens));

    public static HashSet<string> KindOf(Food food, IReadOnlyList<string> tokens) =>
        Segments(food.Description).Take(3).SelectMany(s => s)
            .Where(w => w.Length > 1 && !IsNeutral(w) && !PreferredBonus.ContainsKey(w) && !tokens.Any(t => WordMatches(w, t)))
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsNeutral(string word) =>
        NeutralWords.Contains(word) || CookedWords.Contains(word) || RawWords.Contains(word);

    private static List<List<string>> Segments(string description) =>
        description.Split(',').Select(segment => Words(segment)).ToList();

    private static bool WordMatches(string word, string token) =>
        word == token || (word.StartsWith(token, StringComparison.Ordinal) && word.Length <= token.Length + 2);

    private static List<string> Words(string text) =>
        WordPattern().Matches(FoodText.Fold(text)).Select(m => Singular(m.Value)).ToList();

    private static string Singular(string word) =>
        word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) ? word[..^3] + "y"
        : word.Length > 4 && (word.EndsWith("oes", StringComparison.Ordinal) || word.EndsWith("ches", StringComparison.Ordinal)
            || word.EndsWith("shes", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)) ? word[..^2]
        : word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1]
        : word;

    [GeneratedRegex("[a-z]+")]
    private static partial Regex WordPattern();
}
