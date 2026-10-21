using System.Globalization;
using System.Text;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Import;

public sealed record ImportResult(int FoodsImported, int FoodsSkipped, int NutrientRows, int PortionRows);

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, doubled quotes, newlines inside quotes).</summary>
public static class CsvReader
{
    public static IEnumerable<IReadOnlyDictionary<string, string>> ReadRecords(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        string[]? header = null;
        foreach (var row in ReadRows(reader))
        {
            if (header is null)
            {
                header = row;
                continue;
            }

            var record = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
            {
                record[header[i]] = i < row.Length ? row[i] : "";
            }

            yield return record;
        }
    }

    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;

        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    any = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (any || field.Length > 0)
                    {
                        fields.Add(field.ToString());
                        yield return fields.ToArray();
                    }

                    fields.Clear();
                    field.Clear();
                    any = false;
                    break;
                default:
                    field.Append(ch);
                    any = true;
                    break;
            }
        }

        if (any || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}

/// <summary>
/// Imports the USDA FoodData Central "SR Legacy" CSV download (public domain, https://fdc.nal.usda.gov/download-datasets)
/// into the local food tables, keeping the FDC id as provenance. Only the five tracked nutrients and the household
/// portions are kept. Safe to re-run: foods that already exist are skipped.
/// </summary>
public sealed class UsdaSrLegacyImporter(AppDbContext db)
{
    private const int BatchSize = 500;

    // FDC nutrient ids and the exact names/units we expect, so a changed file layout fails loudly instead of importing wrong numbers.
    private static readonly (int Id, NutrientType Type, string Name, string Unit)[] Expected =
    [
        (1008, NutrientType.Calories, "Energy", "KCAL"),
        (1003, NutrientType.Protein, "Protein", "G"),
        (1005, NutrientType.Carbohydrates, "Carbohydrate, by difference", "G"),
        (1004, NutrientType.Fat, "Total lipid (fat)", "G"),
        (1079, NutrientType.Fiber, "Fiber, total dietary", "G"),
    ];

    public async Task<ImportResult> ImportAsync(string directory, Action<string>? log, CancellationToken ct)
    {
        string File(string name) => Path.Combine(directory, name);
        foreach (var required in new[] { "food.csv", "food_nutrient.csv", "nutrient.csv", "food_category.csv", "food_portion.csv", "measure_unit.csv" })
        {
            if (!System.IO.File.Exists(File(required)))
            {
                throw new FileNotFoundException($"'{required}' not found in '{directory}'. Pass the folder of the extracted SR Legacy CSV download.");
            }
        }

        ValidateNutrientDefinitions(File("nutrient.csv"));

        var categories = CsvReader.ReadRecords(File("food_category.csv")).ToDictionary(r => r["id"], r => r["description"]);
        var units = CsvReader.ReadRecords(File("measure_unit.csv")).ToDictionary(r => r["id"], r => r["name"].Trim().ToLowerInvariant());
        var typeById = Expected.ToDictionary(e => e.Id.ToString(CultureInfo.InvariantCulture), e => e.Type);

        log?.Invoke("Reading foods...");
        var foodRows = CsvReader.ReadRecords(File("food.csv"))
            .Where(r => r["data_type"] == "sr_legacy_food")
            .ToDictionary(r => r["fdc_id"], r => r);

        log?.Invoke("Reading nutrients...");
        var nutrients = new Dictionary<string, List<(NutrientType Type, decimal Amount)>>();
        var nutrientRows = 0;
        foreach (var row in CsvReader.ReadRecords(File("food_nutrient.csv")))
        {
            if (!typeById.TryGetValue(row["nutrient_id"], out var type) || !foodRows.ContainsKey(row["fdc_id"]))
            {
                continue;
            }

            if (!decimal.TryParse(row["amount"], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            {
                continue;
            }

            if (!nutrients.TryGetValue(row["fdc_id"], out var list))
            {
                nutrients[row["fdc_id"]] = list = [];
            }

            list.Add((type, amount));
            nutrientRows++;
        }

        log?.Invoke("Reading portions...");
        var portions = new Dictionary<string, List<(decimal Amount, string Unit, string? Description, decimal Grams)>>();
        var portionRows = 0;
        foreach (var row in CsvReader.ReadRecords(File("food_portion.csv")))
        {
            if (!foodRows.ContainsKey(row["fdc_id"])
                || !decimal.TryParse(row["gram_weight"], NumberStyles.Float, CultureInfo.InvariantCulture, out var grams) || grams <= 0
                || !decimal.TryParse(row["amount"], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                continue;
            }

            var unit = units.GetValueOrDefault(row["measure_unit_id"], "undetermined");
            var description = new[] { row["modifier"], row["portion_description"] }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
            if (!portions.TryGetValue(row["fdc_id"], out var list))
            {
                portions[row["fdc_id"]] = list = [];
            }

            list.Add((amount, unit, description is { Length: > 200 } ? description[..200] : description, grams));
            portionRows++;
        }

        var existing = (await db.Set<Food>().Where(f => f.Source == FoodSource.UsdaSrLegacy).Select(f => f.SourceFoodId).ToListAsync(ct)).ToHashSet();

        int imported = 0, skipped = 0, pending = 0;
        foreach (var (fdcId, row) in foodRows)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.Contains(fdcId))
            {
                skipped++;
                continue;
            }

            var description = row["description"].Trim();
            var food = new Food(
                FoodSource.UsdaSrLegacy, fdcId, description.Length > 300 ? description[..300] : description,
                categories.GetValueOrDefault(row["food_category_id"]), FoodRanking.InferPreparation(description));

            foreach (var (type, amount) in nutrients.GetValueOrDefault(fdcId) ?? [])
            {
                food.AddNutrient(type, amount);
            }

            foreach (var (amount, unit, portionDescription, grams) in portions.GetValueOrDefault(fdcId) ?? [])
            {
                food.AddPortion(amount, unit, portionDescription, grams);
            }

            db.Add(food);
            imported++;
            if (++pending >= BatchSize)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending = 0;
                log?.Invoke($"Imported {imported} foods...");
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return new ImportResult(imported, skipped, nutrientRows, portionRows);
    }

    private static void ValidateNutrientDefinitions(string path)
    {
        var byId = CsvReader.ReadRecords(path).ToDictionary(r => r["id"]);
        foreach (var (id, _, name, unit) in Expected)
        {
            if (!byId.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var row)
                || !string.Equals(row["name"], name, StringComparison.Ordinal)
                || !string.Equals(row["unit_name"], unit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"nutrient.csv does not define nutrient {id} as '{name}' ({unit}). The file layout changed; refusing to import.");
            }
        }
    }
}
