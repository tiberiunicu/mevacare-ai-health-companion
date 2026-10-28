using System.IO.Compression;
using System.Text;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;

/// <param name="RowsRead">Products in the file.</param>
/// <param name="InCountries">Products sold in one of the selected countries.</param>
/// <param name="Rejected">Why products were not imported (reason → count), e.g. missing_energy.</param>
public sealed record OpenFoodFactsImportResult(
    long RowsRead, long InCountries, int Imported, int SkippedExisting, int MalformedRows, IReadOnlyDictionary<string, int> Rejected);

/// <summary>
/// Imports products from the Open Food Facts CSV export (tab separated, optionally gzip-compressed:
/// <c>https://static.openfoodfacts.org/data/en.openfoodfacts.org.products.csv.gz</c>) for the selected countries. The file is streamed, never loaded
/// as a whole. Products are validated by <see cref="OpenFoodFactsMapping"/>; invalid ones are counted by reason and skipped. Safe to re-run:
/// products whose barcode is already stored are skipped (they are not updated, so stored values never change under existing meals).
/// The data is published under the Open Database License (ODbL); see docs/decisions/0016-open-food-facts.md for the obligations.
/// </summary>
public sealed class OpenFoodFactsImporter(AppDbContext db)
{
    private const int BatchSize = 1000;

    private static readonly string[] Columns =
    [
        "code", "product_name", "brands", "countries_tags", "categories_tags", "quantity", "serving_size", "serving_quantity",
        "energy-kcal_100g", "energy_100g", "proteins_100g", "carbohydrates_100g", "fat_100g", "fiber_100g",
    ];

    /// <param name="countries">Open Food Facts country names in English, lower case, with hyphens ("romania", "united-kingdom").</param>
    /// <param name="limit">Stop after this many imported products (for trials); null for no limit.</param>
    public async Task<OpenFoodFactsImportResult> ImportAsync(
        string path, IReadOnlyCollection<string> countries, int? limit, Action<string>? log, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' not found. Pass the Open Food Facts CSV export (.csv or .csv.gz).");
        }

        var tags = countries.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToList();
        if (tags.Count == 0 || tags.Any(c => !c.All(ch => char.IsAsciiLetterLower(ch) || ch == '-')))
        {
            throw new ArgumentException("Give country names in English, lower case, separated by commas (for example: romania,germany,united-kingdom).", nameof(countries));
        }

        var wanted = tags.Select(c => "en:" + c).ToList();
        var existing = (await db.Set<Food>().AsNoTracking().Where(f => f.Source == FoodSource.OpenFoodFacts).Select(f => f.SourceFoodId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        log?.Invoke($"Selected countries: {string.Join(", ", tags)}. Products already stored: {existing.Count}.");

        using var file = File.OpenRead(path);
        using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20);

        var header = (await reader.ReadLineAsync(ct))?.Split('\t') ?? throw new InvalidDataException("The file is empty.");
        var indexes = Columns.Select(c => Array.IndexOf(header, c)).ToArray();
        var missing = Columns.Where((_, i) => indexes[i] < 0).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"The export does not have the expected columns ({string.Join(", ", missing)}); the file layout changed, refusing to import.");
        }

        var countriesSlot = Array.IndexOf(Columns, "countries_tags");
        var slotByColumn = indexes.Select((column, slot) => (column, slot)).ToDictionary(x => x.column, x => x.slot);
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        long rowsRead = 0, inCountries = 0;
        int imported = 0, skipped = 0, malformed = 0, pending = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            rowsRead++;
            var fields = Split(line, slotByColumn, header.Length, Columns.Length);
            if (fields is null)
            {
                malformed++;
                continue;
            }

            if (!InAnyCountry(fields[countriesSlot], wanted))
            {
                continue;
            }

            inCountries++;
            var product = ToProduct(fields);
            if (product.Code is { } code && existing.Contains(code))
            {
                skipped++;
                continue;
            }

            if (!OpenFoodFactsMapping.TryCreateFood(product, out var food, out var reason))
            {
                rejected[reason!] = rejected.GetValueOrDefault(reason!) + 1;
                continue;
            }

            existing.Add(food!.SourceFoodId);
            db.Add(food);
            imported++;
            if (++pending >= BatchSize)
            {
                await Flush(ct);
                pending = 0;
                log?.Invoke($"Imported {imported} products ({rowsRead} rows read)...");
            }

            if (limit is { } max && imported >= max)
            {
                break;
            }
        }

        await Flush(ct);
        return new OpenFoodFactsImportResult(rowsRead, inCountries, imported, skipped, malformed, rejected);
    }

    private async Task Flush(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static bool InAnyCountry(string tagsField, List<string> wanted)
    {
        foreach (var tag in tagsField.Split(',', StringSplitOptions.TrimEntries))
        {
            if (wanted.Contains(tag, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The values of the needed columns, in the order of <see cref="Columns"/>; null when the row has a different number of columns.</summary>
    private static string[]? Split(string line, Dictionary<int, int> wanted, int columnCount, int valueCount)
    {
        var result = new string[valueCount];
        var column = 0;
        var start = 0;
        while (true)
        {
            var end = line.IndexOf('\t', start);
            var stop = end < 0 ? line.Length : end;
            if (wanted.TryGetValue(column, out var slot))
            {
                result[slot] = line[start..stop];
            }

            column++;
            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return column == columnCount ? result : null;
    }

    private static OffProduct ToProduct(string[] f) => new(
        Code: f[0], ProductName: f[1], Brands: f[2], CategoryTag: f[4], Quantity: f[5], ServingSize: f[6],
        ServingQuantityG: OpenFoodFactsMapping.ParseDecimal(f[7]), EnergyKcal: OpenFoodFactsMapping.ParseDecimal(f[8]),
        EnergyKj: OpenFoodFactsMapping.ParseDecimal(f[9]), Protein: OpenFoodFactsMapping.ParseDecimal(f[10]),
        Carbohydrates: OpenFoodFactsMapping.ParseDecimal(f[11]), Fat: OpenFoodFactsMapping.ParseDecimal(f[12]),
        Fiber: OpenFoodFactsMapping.ParseDecimal(f[13]));
}
