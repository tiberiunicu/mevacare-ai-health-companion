using System.IO.Compression;
using System.Net;
using System.Text;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;
using MevaCare.AI.HealthCompanion.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.UnitTests.Nutrition;

public class FoodTextTests
{
    [Theory]
    [InlineData("Brânză de vaci", "branza de vaci")]
    [InlineData("ȘUNCĂ Țărănească", "sunca taraneasca")]
    [InlineData("Süßigkeiten Łódź", "sussigkeiten lodz")]
    [InlineData("Cottage cheese", "cottage cheese")]
    public void Fold_removes_diacritics_and_case(string text, string expected) => Assert.Equal(expected, FoodText.Fold(text));

    [Theory]
    [InlineData("5941238007810", true)]
    [InlineData(" 5941238007810 ", true)]
    [InlineData("96385074", true)]
    [InlineData("123456", false)]
    [InlineData("59412380078ab", false)]
    [InlineData("cottage cheese", false)]
    public void Barcodes_are_recognised_only_as_the_whole_query(string text, bool expected) => Assert.Equal(expected, FoodText.IsBarcode(text));

    [Fact]
    public void The_search_text_holds_name_brand_and_barcode_folded_and_is_capped()
    {
        Assert.Equal("branza hochland 5941238007810", FoodText.BuildSearchText("Brânză", "Hochland", "5941238007810"));
        Assert.Equal(FoodText.MaxSearchTextLength, FoodText.BuildSearchText(new string('x', 900), null, null).Length);
    }
}

public class OpenFoodFactsMappingTests
{
    private static OffProduct Hochland() => new(
        Code: "5941238007810", ProductName: "Hochland Cottage cheese", Brands: "Hochland, Hochland Romania", CategoryTag: "en:dairies,en:cheeses,en:cottage-cheeses",
        Quantity: "175 g,", ServingSize: null, ServingQuantityG: null, EnergyKcal: 95m, EnergyKj: null, Protein: 12m, Carbohydrates: 2m, Fat: 4.5m, Fiber: 0m);

    [Fact]
    public void A_complete_product_becomes_a_food_with_provenance_and_values_per_100_g()
    {
        Assert.True(OpenFoodFactsMapping.TryCreateFood(Hochland(), out var food, out var rejection), rejection);

        Assert.Equal(FoodSource.OpenFoodFacts, food!.Source);
        Assert.Equal("5941238007810", food.SourceFoodId);
        Assert.Equal("5941238007810", food.Barcode);
        Assert.Equal("Hochland", food.Brand);
        Assert.Equal("Cottage cheeses", food.Category);
        Assert.True(food.IsBranded);
        Assert.Equal(12m, food.Nutrients.Single(n => n.Type == NutrientType.Protein).AmountPer100g);
        Assert.Equal(95m, food.Nutrients.Single(n => n.Type == NutrientType.Calories).AmountPer100g);
        Assert.Contains("hochland", food.SearchText);
    }

    [Fact]
    public void The_package_weight_is_stored_as_a_portion_when_it_is_a_plain_weight()
    {
        OpenFoodFactsMapping.TryCreateFood(Hochland(), out var food, out _);

        var package = Assert.Single(food!.Portions);
        Assert.Equal("package", package.Unit);
        Assert.Equal(175m, package.GramWeight);
        Assert.Equal("175 g", package.Description);   // the source says "175 g,"; stray punctuation is dropped
    }

    [Theory]
    [InlineData("175 g", 175)]
    [InlineData("0.5 kg", 500)]
    [InlineData("1,5 kg", 1500)]
    [InlineData("175 g,", 175)]
    public void Package_weights_in_grams_or_kilograms_are_read(string quantity, int expected) =>
        Assert.Equal(expected, OpenFoodFactsMapping.PackageGrams(quantity));

    [Theory]
    [InlineData("6 x 125 g")]
    [InlineData("500 ml")]
    [InlineData("a pack")]
    [InlineData("1 g")]
    [InlineData("")]
    [InlineData(null)]
    public void Package_weights_are_never_guessed(string? quantity) => Assert.Null(OpenFoodFactsMapping.PackageGrams(quantity));

    [Theory]
    [InlineData(0.04)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public void An_implausible_serving_size_is_ignored_but_the_product_is_kept(double serving)
    {
        var product = Hochland() with { ServingQuantityG = (decimal)serving, Quantity = null };

        Assert.True(OpenFoodFactsMapping.TryCreateFood(product, out var food, out _));
        Assert.Empty(food!.Portions);
    }

    [Fact]
    public void A_plausible_serving_size_becomes_a_portion()
    {
        var product = Hochland() with { ServingQuantityG = 30m, ServingSize = "30 g", Quantity = null };

        OpenFoodFactsMapping.TryCreateFood(product, out var food, out _);

        var serving = Assert.Single(food!.Portions);
        Assert.Equal("serving", serving.Unit);
        Assert.Equal(30m, serving.GramWeight);
    }

    [Fact]
    public void Energy_in_kilojoules_is_converted_when_kilocalories_are_missing()
    {
        var product = Hochland() with { EnergyKcal = null, EnergyKj = 418.4m };

        Assert.True(OpenFoodFactsMapping.TryCreateFood(product, out var food, out _));
        Assert.Equal(100m, food!.Nutrients.Single(n => n.Type == NutrientType.Calories).AmountPer100g);
    }

    [Fact]
    public void Without_an_energy_value_the_calories_are_calculated_from_the_macronutrients_and_the_food_is_flagged()
    {
        var product = Hochland() with { EnergyKcal = null, EnergyKj = null, Fiber = 1m };

        Assert.True(OpenFoodFactsMapping.TryCreateFood(product, out var food, out _));

        Assert.True(food!.EnergyDerived);
        Assert.Equal(98.5m, food.Nutrients.Single(n => n.Type == NutrientType.Calories).AmountPer100g);   // 4×12 + 4×2 + 9×4.5 + 2×1
    }

    [Fact]
    public void Reported_energy_is_never_replaced_by_a_calculation()
    {
        OpenFoodFactsMapping.TryCreateFood(Hochland(), out var food, out _);

        Assert.False(food!.EnergyDerived);
        Assert.Equal(95m, food.Nutrients.Single(n => n.Type == NutrientType.Calories).AmountPer100g);
    }

    [Fact]
    public void A_nutrient_the_product_does_not_report_has_no_row_so_it_is_unknown_not_zero()
    {
        var product = Hochland() with { Fiber = null };

        OpenFoodFactsMapping.TryCreateFood(product, out var food, out _);

        Assert.DoesNotContain(food!.Nutrients, n => n.Type == NutrientType.Fiber);
        Assert.Contains(food.Nutrients, n => n.Type == NutrientType.Protein);
    }

    [Theory]
    [MemberData(nameof(RejectedProducts))]
    public void Missing_or_implausible_data_is_rejected_instead_of_stored(string reason, OffProduct product)
    {
        Assert.False(OpenFoodFactsMapping.TryCreateFood(product, out var food, out var rejection));
        Assert.Null(food);
        Assert.Equal(reason, rejection);
    }

    public static IEnumerable<object[]> RejectedProducts()
    {
        var ok = Hochland();
        yield return ["invalid_barcode", ok with { Code = "12ab" }];
        yield return ["invalid_barcode", ok with { Code = "123" }];
        yield return ["invalid_barcode", ok with { Code = null }];
        yield return ["missing_name", ok with { ProductName = "  " }];
        yield return ["missing_energy", ok with { EnergyKcal = null, EnergyKj = null, Carbohydrates = null }];
        yield return ["implausible_energy", ok with { EnergyKcal = 1500m }];
        yield return ["implausible_energy", ok with { EnergyKcal = -5m }];
        yield return ["no_macronutrients", ok with { Protein = null, Carbohydrates = null, Fat = null }];
        yield return ["implausible_macronutrients", ok with { Protein = 120m }];
        yield return ["implausible_macronutrients", ok with { Protein = 60m, Carbohydrates = 40m, Fat = 30m }];
        yield return ["inconsistent_energy", ok with { EnergyKcal = 10m }];
    }

    [Fact]
    public void Energy_above_what_the_macronutrients_explain_is_allowed_because_of_alcohol()
    {
        var wine = Hochland() with { ProductName = "Red wine", EnergyKcal = 85m, Protein = 0.1m, Carbohydrates = 2.6m, Fat = 0m, Fiber = null };

        Assert.True(OpenFoodFactsMapping.TryCreateFood(wine, out _, out _));
    }

    [Fact]
    public void Free_text_from_outside_is_cleaned_before_it_is_stored()
    {
        var product = Hochland() with { ProductName = "  Hochland\tCottage\ncheese\u0007  ", Brands = " , Hochland ,Other" };

        OpenFoodFactsMapping.TryCreateFood(product, out var food, out _);

        Assert.Equal("Hochland Cottage cheese", food!.Description);
        Assert.Equal("Hochland", food.Brand);
    }
}

public class BrandedFoodSearchTests
{
    private static Food Usda(string id, string description, string? category = null) =>
        new(FoodSource.UsdaSrLegacy, id, description, category, FoodRanking.InferPreparation(description));

    private static Food Product(string barcode, string name, string brand, decimal kcal = 100)
    {
        var food = new Food(FoodSource.OpenFoodFacts, barcode, name, "Cheeses", PreparationState.Unspecified, brand, barcode);
        food.AddNutrient(NutrientType.Calories, kcal);
        food.AddNutrient(NutrientType.Protein, 10);
        return food;
    }

    private static async Task<AppDbContext> SeedAsync(params Food[] foods)
    {
        var db = TestDb.Create();
        db.AddRange(foods);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task A_brand_in_the_query_finds_the_product_that_the_generic_database_does_not_have()
    {
        var hochland = Product("5941238007810", "Hochland Cottage cheese", "Hochland");
        await using var db = await SeedAsync(Usda("1", "Cheese, cottage, creamed, large or small curd", "Dairy and Egg Products"), hochland);

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("cottage cheese hochland"), default);

        Assert.Equal(hochland.Id, Assert.Single(result.Matches).Food.Id);
    }

    [Fact]
    public async Task Without_a_brand_the_generic_food_ranks_before_packaged_products()
    {
        var generic = Usda("1", "Cheese, cottage, creamed, large or small curd", "Dairy and Egg Products");
        await using var db = await SeedAsync(Product("5941238007810", "Hochland Cottage cheese", "Hochland"), generic, Product("4001234567890", "Cottage cheese", "Milbona"));

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("cottage cheese"), default);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(generic.Id, result.Matches[0].Food.Id);
    }

    [Theory]
    [InlineData("rice", "Sun Rice", "Rice")]
    [InlineData("chicken breast", "Chicken Breast", "Chicken breast hot and spicy")]
    [InlineData("egg", "Eggo", "Egg waffles")]
    public async Task A_product_whose_brand_or_name_equals_the_query_never_outranks_the_generic_food(string query, string brand, string productName)
    {
        var generic = query switch
        {
            "rice" => Usda("1", "Rice, white, long-grain, regular, enriched, cooked", "Cereal Grains and Pasta"),
            "chicken breast" => Usda("2", "Chicken, broilers or fryers, breast, meat only, cooked, roasted", "Poultry Products"),
            _ => Usda("3", "Egg, whole, cooked, hard-boiled", "Dairy and Egg Products"),
        };
        await using var db = await SeedAsync(Product("4000000000001", productName, brand), Product("4000000000002", brand, brand), generic);

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery(query), default);

        Assert.Equal(generic.Id, result.Matches[0].Food.Id);
    }

    [Fact]
    public async Task Names_with_diacritics_are_found_without_typing_them()
    {
        var branza = Product("5941238013385", "Brânză proaspătă de vaci", "Napolact");
        await using var db = await SeedAsync(branza, Usda("1", "Cheese, cottage, creamed"));
        var catalog = new LocalFoodCatalog(db);

        var plain = await catalog.SearchAsync(new FoodQuery("branza vaci"), default);
        var accented = await catalog.SearchAsync(new FoodQuery("brânză"), default);

        Assert.Equal(branza.Id, Assert.Single(plain.Matches).Food.Id);
        Assert.Equal(branza.Id, Assert.Single(accented.Matches).Food.Id);
    }

    [Fact]
    public async Task A_barcode_finds_exactly_that_product()
    {
        var hochland = Product("5941238007810", "Hochland Cottage cheese", "Hochland");
        await using var db = await SeedAsync(hochland, Product("4001234567890", "Cottage cheese", "Milbona"));

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("5941238007810", IncludeNutrients: true), default);

        var match = Assert.Single(result.Matches);
        Assert.Equal(hochland.Id, match.Food.Id);
        Assert.Contains(match.Food.Nutrients, n => n.Type == NutrientType.Calories);
    }

    [Fact]
    public async Task A_barcode_that_is_not_stored_finds_nothing()
    {
        await using var db = await SeedAsync(Product("5941238007810", "Hochland Cottage cheese", "Hochland"));

        Assert.Empty((await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("4006381333931"), default)).Matches);
    }

    [Fact]
    public async Task Generic_foods_cannot_be_pushed_out_of_the_candidate_window_by_many_products()
    {
        var generic = Usda("1", "Cheese, cottage, creamed, large or small curd");
        var products = Enumerable.Range(0, 600).Select(i => Product((4000000000000L + i).ToString(), "Cottage cheese", $"Brand{i}")).ToArray();
        await using var db = await SeedAsync([generic, .. products]);

        var result = await new LocalFoodCatalog(db).SearchAsync(new FoodQuery("cottage cheese"), default);

        Assert.Equal(generic.Id, result.Matches[0].Food.Id);
    }
}

public class OpenFoodFactsImporterTests : IDisposable
{
    private static readonly string[] Header =
    [
        "code", "url", "product_name", "brands", "categories_tags", "countries_tags", "quantity", "serving_size", "serving_quantity",
        "energy-kcal_100g", "energy_100g", "proteins_100g", "carbohydrates_100g", "fat_100g", "fiber_100g",
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mevacare-off-" + Guid.NewGuid().ToString("N"));

    public OpenFoodFactsImporterTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Row(string code, string name, string brand, string countries, string kcal = "95") =>
        string.Join('\t', code, "https://example.test/" + code, name, brand, "en:cheeses,en:cottage-cheeses", countries, "175 g", "", "", kcal, "", "12", "2", "4.5", "0");

    private string Write(string name, bool gzip, string[] header, params string[] rows)
    {
        var path = Path.Combine(_directory, name);
        var text = string.Join('\n', [string.Join('\t', header), .. rows]) + "\n";
        if (gzip)
        {
            using var file = File.Create(path);
            using var zip = new GZipStream(file, CompressionLevel.Fastest);
            zip.Write(Encoding.UTF8.GetBytes(text));
        }
        else
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        return path;
    }

    private string SampleFile(bool gzip = true) => Write(gzip ? "off.csv.gz" : "off.csv", gzip, Header,
        Row("5941238007810", "Hochland Cottage cheese", "Hochland", "en:romania"),
        Row("4001234567890", "Hüttenkäse", "Milbona", "en:germany,en:romania"),
        Row("3000000000001", "Fromage blanc", "Danone", "en:france"),
        Row("5000000000002", "Impossible bar", "X", "en:romania", kcal: "5000"),
        "5000000000003\tbroken row",
        Row("5941238007810", "Duplicate in the same file", "Hochland", "en:romania"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_valid_products_of_the_selected_countries_are_imported(bool gzip)
    {
        await using var db = TestDb.Create();

        var result = await new OpenFoodFactsImporter(db).ImportAsync(SampleFile(gzip), ["romania"], null, null, default);

        Assert.Equal(6, result.RowsRead);
        Assert.Equal(4, result.InCountries);          // not the French product, not the broken row
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.SkippedExisting);      // the duplicate barcode inside the file
        Assert.Equal(1, result.MalformedRows);
        Assert.Equal(1, result.Rejected["implausible_energy"]);
        Assert.Equal(["4001234567890", "5941238007810"], await db.Set<Food>().OrderBy(f => f.SourceFoodId).Select(f => f.SourceFoodId).ToListAsync());
    }

    [Fact]
    public async Task Imported_products_are_searchable_by_brand_and_without_diacritics()
    {
        await using var db = TestDb.Create();
        await new OpenFoodFactsImporter(db).ImportAsync(SampleFile(), ["romania"], null, null, default);
        var catalog = new LocalFoodCatalog(db);

        var byBrand = await catalog.SearchAsync(new FoodQuery("cottage cheese hochland"), default);
        var byFoldedName = await catalog.SearchAsync(new FoodQuery("huttenkase"), default);

        Assert.Equal("Hochland Cottage cheese", Assert.Single(byBrand.Matches).Food.Description);
        Assert.Equal("Hüttenkäse", Assert.Single(byFoldedName.Matches).Food.Description);
    }

    [Fact]
    public async Task Running_the_import_again_does_not_change_or_duplicate_anything()
    {
        await using var db = TestDb.Create();
        var importer = new OpenFoodFactsImporter(db);
        var file = SampleFile();
        await importer.ImportAsync(file, ["romania"], null, null, default);

        var again = await importer.ImportAsync(file, ["romania"], null, null, default);

        Assert.Equal(0, again.Imported);
        Assert.Equal(3, again.SkippedExisting);
        Assert.Equal(2, await db.Set<Food>().CountAsync());
    }

    [Fact]
    public async Task The_limit_stops_the_import_early()
    {
        await using var db = TestDb.Create();

        var result = await new OpenFoodFactsImporter(db).ImportAsync(SampleFile(), ["romania"], 1, null, default);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, await db.Set<Food>().CountAsync());
    }

    [Fact]
    public async Task Several_countries_can_be_selected()
    {
        await using var db = TestDb.Create();

        var result = await new OpenFoodFactsImporter(db).ImportAsync(SampleFile(), ["france", "germany"], null, null, default);

        Assert.Equal(2, result.Imported);
    }

    [Fact]
    public async Task A_changed_file_layout_is_refused()
    {
        await using var db = TestDb.Create();
        var path = Write("renamed.csv", false, ["code", "product_name", "countries_tags"], "5941238007810\tX\ten:romania");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new OpenFoodFactsImporter(db).ImportAsync(path, ["romania"], null, null, default));

        Assert.Contains("brands", error.Message);
    }

    [Theory]
    [InlineData("Romania!")]
    [InlineData("")]
    [InlineData("ro mania")]
    public async Task Country_names_must_be_plain_open_food_facts_names(string country)
    {
        await using var db = TestDb.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => new OpenFoodFactsImporter(db).ImportAsync(SampleFile(), [country], null, null, default));
    }

    [Fact]
    public async Task A_missing_file_is_reported()
    {
        await using var db = TestDb.Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new OpenFoodFactsImporter(db).ImportAsync(Path.Combine(_directory, "nope.csv.gz"), ["romania"], null, null, default));
    }
}

public class OpenFoodFactsClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenFoodFactsClient Client(FakeHandler handler, OpenFoodFactsOptions? options = null, SlidingRateGate? gate = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://off.example.test/") }, Options.Create(options ?? new OpenFoodFactsOptions()),
            gate ?? new SlidingRateGate(TimeProvider.System), NullLogger<OpenFoodFactsClient>.Instance);

    private const string SearchBody = """
        {"count":2,"hits":[
          {"code":"5941238007810","product_name":"Hochland Cottage cheese","brands":"Hochland","quantity":"175 g,",
           "categories_tags":["en:dairies","en:cottage-cheeses"],
           "nutriments":{"energy-kcal_100g":95,"proteins_100g":"12","carbohydrates_100g":2,"fat_100g":4.5,"fiber_100g":0}},
          {"code":"2942499137516","product_name":"Hochland cheese","brands":["Hochland"],"nutriments":{"energy-kcal_100g":160}}
        ]}
        """;

    [Fact]
    public async Task Search_parses_products_with_numbers_as_numbers_or_text_and_brands_as_text_or_lists()
    {
        var handler = new FakeHandler(_ => Json(SearchBody));

        var products = await Client(handler).SearchAsync("hochland cottage cheese", default);

        Assert.Equal(2, products.Count);
        Assert.Equal("Hochland Cottage cheese", products[0].ProductName);
        Assert.Equal(12m, products[0].Protein);
        Assert.Equal("en:dairies,en:cottage-cheeses", products[0].CategoryTag);
        Assert.Equal("Hochland", products[1].Brands);
        Assert.Null(products[1].Protein);
    }

    [Fact]
    public async Task The_search_text_is_escaped_and_only_the_text_is_sent()
    {
        var handler = new FakeHandler(_ => Json("""{"hits":[]}"""));

        await Client(handler, new OpenFoodFactsOptions { SearchBaseUrl = "https://search.example.test" }).SearchAsync("brânză & lapte", default);

        var uri = Assert.Single(handler.Requests);
        Assert.Contains("q=br%C3%A2nz%C4%83%20%26%20lapte", uri.AbsoluteUri);
        Assert.Contains("langs=en%2Cro", uri.AbsoluteUri);
        Assert.Equal("search.example.test", uri.Host);
    }

    [Fact]
    public async Task A_product_is_read_by_barcode_and_an_unknown_barcode_gives_null()
    {
        var found = new FakeHandler(_ => Json("""{"code":"5941238007810","status":1,"product":{"code":"5941238007810","product_name":"Hochland Cottage cheese","nutriments":{"energy-kcal_100g":95,"proteins_100g":12}}}"""));
        var missing = new FakeHandler(_ => Json("""{"status":0}""", HttpStatusCode.NotFound));

        Assert.Equal("Hochland Cottage cheese", (await Client(found).GetProductAsync("5941238007810", default))!.ProductName);
        Assert.Null(await Client(missing).GetProductAsync("5941238007811", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_failing_provider_becomes_a_service_unavailable_error_without_provider_details(HttpStatusCode status)
    {
        var client = Client(new FakeHandler(_ => Json("secret internal detail", status)));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.SearchAsync("cheese", default));

        Assert.Equal("food_online_unavailable", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task An_unreadable_answer_becomes_a_service_unavailable_error()
    {
        var client = Client(new FakeHandler(_ => Json("<html>not json</html>")));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.SearchAsync("cheese", default));

        Assert.Equal("food_online_unavailable", error.Code);
    }

    [Fact]
    public async Task When_the_lookup_is_turned_off_nothing_is_sent()
    {
        var handler = new FakeHandler(_ => Json(SearchBody));
        var client = Client(handler, new OpenFoodFactsOptions { Enabled = false });

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.SearchAsync("cheese", default));

        Assert.Equal("food_online_disabled", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_server_wide_limit_protects_the_provider_and_recovers_after_a_minute()
    {
        var clock = TestDb.Clock();
        var handler = new FakeHandler(_ => Json("""{"hits":[]}"""));
        var client = Client(handler, new OpenFoodFactsOptions { SearchesPerMinute = 2 }, new SlidingRateGate(clock));

        await client.SearchAsync("a1", default);
        await client.SearchAsync("a2", default);
        await Assert.ThrowsAsync<RateLimitedException>(() => client.SearchAsync("a3", default));
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromSeconds(61));
        await client.SearchAsync("a4", default);
        Assert.Equal(3, handler.Requests.Count);
    }
}

public class OnlineFoodServiceTests
{
    private sealed class FakeClient : IOpenFoodFactsClient
    {
        public List<OffProduct> Products { get; } = [];
        public int ProductReads { get; private set; }

        public Task<IReadOnlyList<OffProduct>> SearchAsync(string text, CancellationToken ct) => Task.FromResult<IReadOnlyList<OffProduct>>(Products.ToList());

        public Task<OffProduct?> GetProductAsync(string barcode, CancellationToken ct)
        {
            ProductReads++;
            return Task.FromResult(Products.FirstOrDefault(p => p.Code == barcode));
        }
    }

    private static OffProduct Hochland(string code = "5941238007810") => new(
        code, "Hochland Cottage cheese", "Hochland", "en:cottage-cheeses", "175 g", null, null, 95m, null, 12m, 2m, 4.5m, 0m);

    private static (OnlineFoodService Service, FakeClient Client, AppDbContext Db) Create(bool consent = true)
    {
        var db = TestDb.Create();
        var client = new FakeClient();
        return (new OnlineFoodService(db, client, new StubConsentService(consent), Options.Create(new OpenFoodFactsOptions { MaxResults = 3 })), client, db);
    }

    [Fact]
    public async Task Search_returns_valid_candidates_only_and_does_not_store_anything()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.AddRange([Hochland(), Hochland("4001234567890") with { EnergyKcal = 9000m }, Hochland() with { ProductName = "Same barcode twice" }]);

        var candidates = await service.SearchAsync(Guid.NewGuid(), "hochland", default);

        var candidate = Assert.Single(candidates);
        Assert.Equal("5941238007810", candidate.Barcode);
        Assert.Equal("Hochland", candidate.Brand);
        Assert.Equal("175 g", candidate.Quantity);
        Assert.Equal(95m, candidate.Per100g.Calories);
        Assert.Null(candidate.FoodId);
        Assert.Empty(db.Set<Food>());
    }

    [Fact]
    public async Task Only_products_containing_every_word_of_the_query_are_offered_and_the_list_is_capped()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.AddRange([
            Hochland("4000000000001") with { ProductName = "Other cottage cheese", Brands = "Milbona" },
            Hochland("4000000000002"), Hochland("4000000000003"), Hochland("4000000000004"), Hochland("4000000000005")]);

        var candidates = await service.SearchAsync(Guid.NewGuid(), "hochland cottage", default);

        Assert.Equal(3, candidates.Count);                                  // MaxResults = 3 in this test
        Assert.All(candidates, c => Assert.Equal("Hochland", c.Brand));
        Assert.DoesNotContain(candidates, c => c.Barcode == "4000000000001");
    }

    [Fact]
    public async Task A_product_without_an_energy_value_is_offered_with_calculated_calories_and_a_flag()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.Add(Hochland() with { EnergyKcal = null });

        var candidate = Assert.Single(await service.SearchAsync(Guid.NewGuid(), "hochland", default));

        Assert.True(candidate.EnergyDerived);
        Assert.Equal(96.5m, candidate.Per100g.Calories);
    }

    [Fact]
    public async Task A_candidate_that_is_already_stored_carries_its_food_id()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.Add(Hochland());
        var added = await service.AddAsync(Guid.NewGuid(), "5941238007810", default);

        var candidates = await service.SearchAsync(Guid.NewGuid(), "hochland", default);

        Assert.Equal(added.FoodId, Assert.Single(candidates).FoodId);
    }

    [Fact]
    public async Task Adding_a_product_stores_it_with_its_provenance_and_is_idempotent()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.Add(Hochland());

        var first = await service.AddAsync(Guid.NewGuid(), "5941238007810", default);
        var second = await service.AddAsync(Guid.NewGuid(), "5941238007810", default);

        Assert.Equal(first.FoodId, second.FoodId);
        Assert.Equal(1, client.ProductReads);                // the second call is served from the local copy
        Assert.Equal("OpenFoodFacts #5941238007810", first.SourceLabel);
        Assert.Equal("Hochland", first.Brand);
        Assert.Equal(12m, first.Per100g.ProteinG);
        var stored = await db.Set<Food>().SingleAsync();
        Assert.Equal(FoodSource.OpenFoodFacts, stored.Source);
    }

    [Fact]
    public async Task Only_the_barcode_is_taken_from_the_client_and_the_values_come_from_the_provider()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.Add(Hochland());

        var added = await service.AddAsync(Guid.NewGuid(), " 5941238007810 ", default);

        Assert.Equal(95m, added.Per100g.Calories);
    }

    [Fact]
    public async Task An_unknown_barcode_is_not_found()
    {
        var (service, _, db) = Create();
        await using var _ = db;

        await Assert.ThrowsAsync<NotFoundException>(() => service.AddAsync(Guid.NewGuid(), "4006381333931", default));
    }

    [Fact]
    public async Task A_product_with_implausible_data_is_not_added()
    {
        var (service, client, db) = Create();
        await using var _ = db;
        client.Products.Add(Hochland() with { EnergyKcal = 9000m });

        var error = await Assert.ThrowsAsync<BadRequestException>(() => service.AddAsync(Guid.NewGuid(), "5941238007810", default));

        Assert.Equal("product_data_unusable", error.Code);
        Assert.Empty(db.Set<Food>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    public async Task A_malformed_barcode_is_a_bad_request(string barcode)
    {
        var (service, _, db) = Create();
        await using var _ = db;

        var error = await Assert.ThrowsAsync<BadRequestException>(() => service.AddAsync(Guid.NewGuid(), barcode, default));

        Assert.Equal("invalid_barcode", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task A_too_short_search_text_is_a_bad_request(string text)
    {
        var (service, _, db) = Create();
        await using var _ = db;

        var error = await Assert.ThrowsAsync<BadRequestException>(() => service.SearchAsync(Guid.NewGuid(), text, default));

        Assert.Equal("invalid_query", error.Code);
    }

    [Fact]
    public async Task Without_the_external_integrations_consent_nothing_is_sent_or_stored()
    {
        var (service, client, db) = Create(consent: false);
        await using var _ = db;
        client.Products.Add(Hochland());

        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.SearchAsync(Guid.NewGuid(), "hochland", default));
        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.AddAsync(Guid.NewGuid(), "5941238007810", default));

        Assert.Equal(0, client.ProductReads);
        Assert.Empty(db.Set<Food>());
    }
}

public class BrandedMealProposalTests
{
    private static async Task<(NutritionAssistantApi Api, AppDbContext Db, Food Generic, Food Product)> SetupAsync()
    {
        var clock = TestDb.Clock();
        var db = TestDb.Create(clock);
        var generic = new Food(FoodSource.UsdaSrLegacy, "1", "Cheese, cottage, creamed, large or small curd", "Dairy and Egg Products", PreparationState.Unspecified);
        generic.AddNutrient(NutrientType.Calories, 98);
        var product = new Food(FoodSource.OpenFoodFacts, "5941238007810", "Hochland Cottage cheese", "Cottage cheeses", PreparationState.Unspecified, "Hochland", "5941238007810");
        product.AddNutrient(NutrientType.Calories, 95);
        db.AddRange(generic, product);
        await db.SaveChangesAsync();
        var consent = new StubConsentService(true);
        var audit = new MevaCare.AI.HealthCompanion.Infrastructure.Auditing.AuditLog(db, clock);
        var targets = new NutritionTargetService(db, consent, audit);
        var meals = new MealService(db, consent, new StubGate(), audit, clock);
        return (new NutritionAssistantApi(new LocalFoodCatalog(db), new NutritionSummaryService(db, consent, targets), consent, targets, meals), db, generic, product);
    }

    private static ParsedMeal Meal(string? brand) => new(null, [new ParsedFoodItem("cottage cheese", 175, "g", "unspecified", "175 g Hochland cottage cheese", brand)]);

    [Fact]
    public async Task A_named_brand_is_matched_to_that_product_when_it_is_in_the_database()
    {
        var (api, db, _, product) = await SetupAsync();
        await using var _ = db;

        var proposal = await api.PrepareMealAsync(Guid.NewGuid(), Meal("Hochland"), default);

        var item = Assert.Single(proposal.Items);
        Assert.Equal("Matched", item.Status);
        Assert.Equal(product.Id, item.Food!.FoodId);
        Assert.Equal(166.25m, item.Nutrients.Calories);
        Assert.DoesNotContain(proposal.Warnings, w => w.Contains("brand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unknown_brand_falls_back_to_the_generic_food_and_says_so()
    {
        var (api, db, generic, _) = await SetupAsync();
        await using var _ = db;

        var proposal = await api.PrepareMealAsync(Guid.NewGuid(), Meal("Napolact"), default);

        var item = Assert.Single(proposal.Items);
        Assert.Equal(generic.Id, item.Food!.FoodId);
        Assert.Contains(proposal.Warnings, w => w.Contains("'Napolact' was not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_packaged_product_used_without_a_named_brand_is_flagged_for_checking()
    {
        var (api, db, _, _) = await SetupAsync();
        await using var _ = db;

        var proposal = await api.PrepareMealAsync(Guid.NewGuid(), new ParsedMeal(null, [new ParsedFoodItem("hochland", 100, "g", "unspecified", "100 g hochland", null)]), default);

        Assert.Equal("Matched", Assert.Single(proposal.Items).Status);
        Assert.Contains(proposal.Warnings, w => w.Contains("packaged product", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_brand_the_generic_food_is_used_and_no_brand_warning_appears()
    {
        var (api, db, generic, _) = await SetupAsync();
        await using var _ = db;

        var proposal = await api.PrepareMealAsync(Guid.NewGuid(), Meal(null), default);

        Assert.Equal(generic.Id, Assert.Single(proposal.Items).Food!.FoodId);
        Assert.DoesNotContain(proposal.Warnings, w => w.Contains("brand", StringComparison.OrdinalIgnoreCase));
    }
}
