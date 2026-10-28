# ADR-0016: Packaged products from Open Food Facts: bulk import by country plus an explicit online lookup

**Status:** Accepted

**Context.** USDA SR Legacy (ADR-0010) is generic US data: it has no brands and no European supermarket products ("Hochland cottage cheese" was not found). The owner wants more
foods, ideally supermarket products across Europe, and the option to look foods up online. Supermarkets do not publish product lists with nutrition values, and extracting them from
their websites would break their terms. Nutrition values must never be invented (ADR-0010), so an LLM or arbitrary web pages are not a source either.

**Decision.**
- **Source: Open Food Facts** (community database, products with barcode, brand, countries and values per 100 g, published under the Open Database License, ODbL). It is the only open source
  of European branded products.
- **Bulk import** (`import-openfoodfacts <csv or csv.gz> <countries> [--limit N]`): streams the official CSV export, keeps only the selected countries, validates every product and stores
  it as a `Food` with `Source = OpenFoodFacts`, brand, barcode and the barcode as source id. Idempotent; existing products are never overwritten (past meals and stored values stay stable).
- **Online lookup, only on request** (`GET /api/nutrition/foods/online/search`, `POST /api/nutrition/foods/online/add`): a search only previews candidates (the official search service, a pool of 100 results filtered so that every query word is in the name or brand); **add** re-reads the
  product by barcode **on the server** and stores it with its provenance. The client sends a barcode, never nutrient values. It needs the *External integrations* consent because the
  search text is sent to a third party; nothing else is sent (no user id, no health data). A server-wide limit keeps the application below Open Food Facts' published limits, and the
  requests carry an identifying User-Agent as their terms ask.
- **Validation** (`OpenFoodFactsMapping`, shared by both paths): barcode of 8, 12, 13 or 14 digits; a name; energy (kcal, or kJ ÷ 4.184) within 0–900 per 100 g, **or, when the product has protein,
  carbohydrates and fat but no energy (common: the owner's Hochland example), calories calculated from them and the food flagged `EnergyDerived`, which makes meal items estimates**; at least one of protein, carbohydrates,
  fat; each ≤ 100 g and their sum ≤ 102 g; energy not far *below* what the macronutrients provide (higher is allowed: alcohol). Free text is cleaned of control characters. Rejections are counted by
  reason. The checks catch impossible data, not a wrongly typed label, so the UI says that product data is contributed by volunteers.
- **Search**: foods are found on a pre-folded column `SearchText` (name + brand + barcode, lower case, no diacritics) because the database collation is accent-sensitive and Romanian names
  ("brânză") must be found as "branza". A barcode typed as the whole query finds exactly that product. Generic (USDA) foods rank before products unless the brand is part of the query, and
  are taken first into the candidate window so a flood of products cannot hide them. The USDA scoring is unchanged (its benchmark still passes); products use a separate, simpler score.
- **Licence handling**: the attribution is shown in the online preview and stored as the source label (`OpenFoodFacts #barcode`). The ODbL requires attribution and share-alike for a
  *publicly distributed derived database*; using the data inside the application is covered. **Redistributing the `Foods` table (for example publishing a database dump) would have to
  follow the ODbL**, and the repository must not contain imported data (it does not).

**Alternatives.** Scraping supermarket sites (terms, fragility); an LLM or generic web search that "finds" nutrition values (unverifiable, conflicts with ADR-0010); importing the whole world (a
million rows and an index problem); a vendor food API (cost, keys); only the live API without storing (no offline use, rate limits, values could change under existing meals).

**Consequences.**
- Quality is that of volunteers' data: incomplete or wrong products exist. The validation removes impossible values only; products stay "estimates by a community", which the UI states.
- Only five nutrients are kept, as before; a missing nutrient is unknown, not zero.
- The substring search scans `SearchText`; it stays fast for hundreds of thousands of rows, but **all of Europe (millions) would need a real index** (SQL Server full-text, or a vector/keyword
  index). Import a few countries and use the online lookup for the rest.
- Matching from chat (`PrepareMeal`) can now return a branded product when a brand is named; products are flagged by their source label in the card.
- The consent text "External integrations" now also covers this lookup (wording extended, policy version not bumped; bump it before real users).
- Open Food Facts availability and terms can change; the lookup degrades to a clear 503 and everything stored locally keeps working.
