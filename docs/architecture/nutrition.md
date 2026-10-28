# Nutrition module

## What it does

Food journal, daily and range summaries, user-set targets, and the deterministic back end of the AI meal logging flow. Code:
`src/backend/MevaCare.AI.HealthCompanion.Modules.Nutrition`; UI: `src/frontend/mevacare-ai-web/src/features/nutrition`.

## Data source and licence

- First source: **USDA FoodData Central, SR Legacy** (7,793 foods, 38,403 nutrient values for the five tracked nutrients, 14,431 household portions).
  FoodData Central data is in the public domain (CC0); cite "U.S. Department of Agriculture, Agricultural Research Service, FoodData Central" where
  the data is shown. Each food keeps its source and FDC id (`UsdaSrLegacy #171477`), displayed in the UI.
- Import: download "SR Legacy" CSV from <https://fdc.nal.usda.gov/download-datasets>, extract it and run
  `dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api -- import-usda <folder>`. It validates that `nutrient.csv` still defines
  nutrients 1008/1003/1005/1004/1079 as expected (refusing to import otherwise), keeps only those five nutrients plus portions, and is safe to re-run
  (existing foods are skipped). The dataset is **not** committed to the repository.
- SR Legacy is a 2018 snapshot of mostly generic US foods. It contains few branded products and no Romanian specialities (see limitations).
- Second source: **Open Food Facts** (packaged products with brand and barcode, ODbL licence, community-contributed data; [ADR-0016](../decisions/0016-open-food-facts-products.md)), added in two ways:
  - *Bulk import for chosen countries*: download the CSV export (`https://static.openfoodfacts.org/data/en.openfoodfacts.org.products.csv.gz`, ~0.8 GB compressed, streamed, do not extract it) and run
    `dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api -- import-openfoodfacts <file> romania,germany,austria [--limit N]` (English country names as Open Food Facts writes them,
    lower case, hyphens). Products are validated (see the ADR); the command prints how many were imported, skipped and rejected per reason. Safe to re-run; existing products are never changed.
  - *Online lookup on request*: in the food picker, after a local search, the user can press "Search packaged products online". The server queries Open Food Facts (`cgi/search.pl`), the user
    chooses a product and the server stores it by barcode (it re-reads the product itself). Requires the External integrations consent; settings under `Nutrition:OpenFoodFacts`
    (`Enabled`, `ContactEmail` for the User-Agent, `TimeoutSeconds`, `MaxResults`, `SearchesPerMinute` ≤ 10 and `ProductReadsPerMinute` for the whole server).
- Other databases (Romanian tables...) plug in as another importer and a new `FoodSource` value; callers use `IFoodCatalog`.

## Data model (schema `nutrition`)

| Table | Notes |
|---|---|
| `Foods` | source, source id (unique together), description, category, `Preparation` (Raw/Cooked/Unspecified, inferred from the description), `Brand` and `Barcode` (packaged products), `SearchText` (name + brand + barcode, lower case, no diacritics: what the search runs on) |
| `FoodNutrients` | (FoodId, Nutrient) → amount **per 100 g**. A nutrient the source does not report has **no row** (unknown ≠ 0) |
| `FoodPortions` | household measures with gram weights, e.g. "1 cup = 158 g" |
| `Meals` | user, type, `EatenAtUtc`, `LocalDate`, `UtcOffsetMinutes` (source time zone), notes, origin (Manual/Assistant), `SourceProposalId` (unique) |
| `MealItems` | name, grams, preparation, estimated flag, source label and a **snapshot** of the five nutrient values calculated at logging time (nullable = unknown); `FoodId` is deliberately not a foreign key |
| `NutritionTargets` | one row per user, every target optional |

Constraints: positive weights ≤ 10,000 g, non-negative nutrients, target ranges. All user data cascades from `Users`.

## Rules that keep it honest

- **Deterministic arithmetic** (`NutrientCalculator`): value = per-100-g × grams / 100, rounded to 2 decimals (away from zero); totals add the rounded item values and
  are rounded to 1 decimal. Totals carry `IncompleteData` (some item lacked a nutrient) and `IncludesEstimates`. The LLM never calculates or supplies nutrients; the server
  rejects client-supplied nutrient numbers for database foods.
- **Snapshots**: changing or re-importing food data never changes past meals.
- **Raw vs cooked**: each food states its state; searching "cooked rice" prefers cooked foods; when the user does not say, categories normally eaten cooked (meat, fish, grains,
  legumes) lean cooked and fruit/vegetables/nuts lean raw, and the proposal says **"assumed"**. 150 g of dry rice is ~2.8× the calories of 150 g cooked rice.
- **Units** (`QuantityConverter`): g, kg, oz, lb convert exactly; cup, tbsp, tsp, slice and piece need a portion recorded for that food; otherwise the item asks for grams.
  Nothing is guessed; weights above 10 kg are refused.
- **Estimates**: items typed by the user without a database food are saved with `IsEstimated`, shown with a badge, and flagged in totals.
- **Unknown foods** stay "not found" in a proposal, are excluded from totals and must be resolved (database food, custom estimate) or removed by the user.
- **Summaries** group by the user's local date. The range average counts **only days with meals** (a day without logging is unknown, not zero).
- Targets are the user's own numbers; the app computes no energy needs and gives no dietary prescriptions.

## Food matching (`FoodRanking`, `LocalFoodCatalog`)

USDA names are "base food, descriptor, descriptor…". The matcher (1) narrows candidates in SQL, (2) requires whole-word matches (rice ≠ licorice), (3) scores: matches in the base
food and early descriptors count more; unrelated words in the first segments and processed-product markers (breaded, canned, powder, flour, bran, sliced, low fat…) count against;
low-priority categories (baby foods, fast food, snacks, deli meats, baked products…) are pushed down; USDA boilerplate ("broilers or fryers", "with added vitamin D") is neutral;
small preferences for common defaults (whole, regular, white, plain, red); raw/cooked as above; (4) flags a result as **ambiguous** only when the two best matches are close **and** are
different kinds of food (white vs brown rice, dairy vs soy yogurt), not mere cooking variants.

Measured on real SR Legacy descriptions (`tests/UnitTests/Nutrition/FoodRankingTests`, fixture `usda-sr-legacy-candidates.json`): for 21 common queries (25 raw/cooked scenarios) the expected generic food is
first (chicken breast, rice, egg, milk, banana, olive oil, tomato, carrot, butter, almonds, honey, yogurt, bread, oats, potato, pasta, salmon, ground beef, broccoli, turkey breast, orange).
The first version of the heuristic picked a deli "chicken breast roll" for "chicken breast" and "rice crackers" for "rice"; the benchmark exists to prevent such regressions.

## Limitations

- Heuristic matching can still pick a poor first result for uncommon queries; alternatives and a search box are always offered, and the user confirms every AI-prepared meal.
- Romanian foods and branded products are not in SR Legacy; the assistant translates names to generic English, which works for staples, not for local dishes. Packaged products come from Open Food Facts
  (import or online lookup); traditional dishes without a barcode are still missing.
- Open Food Facts data is contributed by volunteers: values can be incomplete or wrong. The importer and the online path reject impossible values only. Products rank after generic foods unless the query names the brand.
- The local search scans `SearchText` with substring matching: fine for hundreds of thousands of rows (import a few countries), not for all of Europe without a real index (SQL Server full-text or a dedicated search index).
- Only five nutrients are stored (calories, protein, carbohydrates, fat, fibre): no sugars, sodium, vitamins.
- Liquids in ml need a gram weight (no densities are assumed). Piece sizes depend on the portions recorded by USDA.
- No barcode scan, photo recognition, recipes, or favourites yet.
