using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Import;
using MevaCare.AI.HealthCompanion.UnitTests.Support;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.UnitTests.Nutrition;

public class CsvReaderTests
{
    private static List<string[]> Read(string text) => CsvReader.ReadRows(new StringReader(text)).ToList();

    [Fact]
    public void Quoted_fields_may_contain_commas_quotes_and_newlines()
    {
        var rows = Read("a,b,c\n\"x, y\",\"he said \"\"hi\"\"\",\"line1\nline2\"\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["x, y", "he said \"hi\"", "line1\nline2"], rows[1]);
    }

    [Fact]
    public void Empty_fields_and_windows_line_endings_are_handled()
    {
        var rows = Read("\"1\",\"\",\"3\"\r\n\"4\",\"5\",\"\"\r\n");

        Assert.Equal(["1", "", "3"], rows[0]);
        Assert.Equal(["4", "5", ""], rows[1]);
    }

    [Fact]
    public void A_last_line_without_a_newline_is_still_read() =>
        Assert.Equal(["a", "b"], Read("a,b")[0]);
}

public sealed class UsdaFixtureDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mevacare-usda-" + Guid.NewGuid().ToString("N"));

    public UsdaFixtureDirectory(string? energyUnit = "KCAL")
    {
        Directory.CreateDirectory(Path);
        Write("nutrient.csv",
            "\"id\",\"name\",\"unit_name\",\"nutrient_nbr\",\"rank\"",
            $"\"1008\",\"Energy\",\"{energyUnit}\",\"208\",\"300.0\"",
            "\"1003\",\"Protein\",\"G\",\"203\",\"600.0\"",
            "\"1004\",\"Total lipid (fat)\",\"G\",\"204\",\"800.0\"",
            "\"1005\",\"Carbohydrate, by difference\",\"G\",\"205\",\"1110.0\"",
            "\"1079\",\"Fiber, total dietary\",\"G\",\"291\",\"1200.0\"",
            "\"1051\",\"Water\",\"G\",\"255\",\"100.0\"");
        Write("food_category.csv", "\"id\",\"code\",\"description\"", "\"5\",\"0500\",\"Poultry Products\"");
        Write("measure_unit.csv", "\"id\",\"name\"", "\"9999\",\"undetermined\"", "\"1000\",\"cup\"");
        Write("food.csv",
            "\"fdc_id\",\"data_type\",\"description\",\"food_category_id\",\"publication_date\"",
            "\"171477\",\"sr_legacy_food\",\"Chicken, broilers or fryers, breast, meat only, cooked, roasted\",\"5\",\"2019-04-01\"",
            "\"900001\",\"foundation_food\",\"Not an SR Legacy food\",\"5\",\"2019-04-01\"",
            "\"171478\",\"sr_legacy_food\",\"Chicken, test food without fibre\",\"5\",\"2019-04-01\"");
        Write("food_nutrient.csv",
            "\"id\",\"fdc_id\",\"nutrient_id\",\"amount\",\"data_points\",\"derivation_id\",\"min\",\"max\",\"median\",\"footnote\",\"min_year_acquired\"",
            "\"1\",\"171477\",\"1008\",\"165\",\"0\",\"49\",\"\",\"\",\"\",\"\",\"\"",
            "\"2\",\"171477\",\"1003\",\"31.02\",\"16\",\"\",\"\",\"\",\"\",\"\",\"\"",
            "\"3\",\"171477\",\"1004\",\"3.57\",\"16\",\"\",\"\",\"\",\"\",\"\",\"\"",
            "\"4\",\"171477\",\"1005\",\"0\",\"0\",\"68\",\"\",\"\",\"\",\"\",\"\"",
            "\"5\",\"171477\",\"1079\",\"0\",\"0\",\"68\",\"\",\"\",\"\",\"\",\"\"",
            "\"6\",\"171477\",\"1051\",\"65\",\"0\",\"\",\"\",\"\",\"\",\"\",\"\"",
            "\"7\",\"171478\",\"1008\",\"100\",\"0\",\"\",\"\",\"\",\"\",\"\",\"\"",
            "\"8\",\"900001\",\"1008\",\"1\",\"0\",\"\",\"\",\"\",\"\",\"\",\"\"");
        Write("food_portion.csv",
            "\"id\",\"fdc_id\",\"seq_num\",\"amount\",\"measure_unit_id\",\"portion_description\",\"modifier\",\"gram_weight\",\"data_points\",\"footnote\",\"min_year_acquired\"",
            "\"1\",\"171477\",\"1\",\"1\",\"9999\",\"\",\"cup, chopped or diced\",\"140\",\"\",\"\",\"\"",
            "\"2\",\"171477\",\"2\",\"1\",\"1000\",\"\",\"\",\"0\",\"\",\"\",\"\"");
    }

    private void Write(string name, params string[] lines) => File.WriteAllText(System.IO.Path.Combine(Path, name), string.Join("\n", lines) + "\n");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class UsdaImporterTests
{
    [Fact]
    public async Task Only_sr_legacy_foods_are_imported_with_the_five_nutrients_portions_and_provenance()
    {
        using var files = new UsdaFixtureDirectory();
        await using var db = TestDb.Create();

        var result = await new UsdaSrLegacyImporter(db).ImportAsync(files.Path, null, default);

        Assert.Equal(2, result.FoodsImported);
        var chicken = await db.Set<Food>().Include(f => f.Nutrients).Include(f => f.Portions).SingleAsync(f => f.SourceFoodId == "171477");
        Assert.Equal(FoodSource.UsdaSrLegacy, chicken.Source);
        Assert.Equal("Poultry Products", chicken.Category);
        Assert.Equal(PreparationState.Cooked, chicken.Preparation);
        Assert.Equal(5, chicken.Nutrients.Count);   // water is not kept
        Assert.Equal(165m, chicken.Nutrients.Single(n => n.Type == NutrientType.Calories).AmountPer100g);
        Assert.Equal(31.02m, chicken.Nutrients.Single(n => n.Type == NutrientType.Protein).AmountPer100g);
        var portion = Assert.Single(chicken.Portions);   // the zero-gram portion is dropped
        Assert.Equal(140m, portion.GramWeight);
        Assert.Equal("cup, chopped or diced", portion.Description);
        Assert.False(await db.Set<Food>().AnyAsync(f => f.SourceFoodId == "900001"));
    }

    [Fact]
    public async Task A_nutrient_the_source_does_not_report_is_absent_not_zero()
    {
        using var files = new UsdaFixtureDirectory();
        await using var db = TestDb.Create();

        await new UsdaSrLegacyImporter(db).ImportAsync(files.Path, null, default);

        var food = await db.Set<Food>().Include(f => f.Nutrients).SingleAsync(f => f.SourceFoodId == "171478");
        Assert.Equal([NutrientType.Calories], food.Nutrients.Select(n => n.Type));
    }

    [Fact]
    public async Task Running_the_import_twice_does_not_duplicate_foods()
    {
        using var files = new UsdaFixtureDirectory();
        await using var db = TestDb.Create();
        var importer = new UsdaSrLegacyImporter(db);

        await importer.ImportAsync(files.Path, null, default);
        var second = await importer.ImportAsync(files.Path, null, default);

        Assert.Equal(0, second.FoodsImported);
        Assert.Equal(2, second.FoodsSkipped);
        Assert.Equal(2, await db.Set<Food>().CountAsync());
    }

    [Fact]
    public async Task A_changed_nutrient_definition_stops_the_import_instead_of_storing_wrong_numbers()
    {
        using var files = new UsdaFixtureDirectory(energyUnit: "kJ");
        await using var db = TestDb.Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new UsdaSrLegacyImporter(db).ImportAsync(files.Path, null, default));

        Assert.Contains("1008", ex.Message, StringComparison.Ordinal);
        Assert.Empty(db.Set<Food>());
    }

    [Fact]
    public async Task A_wrong_folder_gives_a_clear_error()
    {
        await using var db = TestDb.Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new UsdaSrLegacyImporter(db).ImportAsync(Path.GetTempPath() + "does-not-exist", null, default));
    }
}
