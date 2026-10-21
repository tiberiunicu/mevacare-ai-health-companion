# ADR-0010: USDA SR Legacy as the first food source, imported locally, matched by a transparent heuristic

**Status:** Accepted

**Context.** Nutrition values must come from a verified database, must not be invented, and the design must allow other databases (including Romanian products) later. The USDA FoodData
Central API needs a key and network access at runtime; the downloadable datasets are public domain.

**Decision.**
- Import the **SR Legacy CSV download** into local tables with a CLI command, keeping the FDC id as provenance. The importer validates the nutrient definitions it depends on and skips existing foods.
  The app then works offline and the food data cannot change under users' feet. (The live API can be added later as another source behind `IFoodCatalog`.)
- Store only five nutrients per 100 g plus portions. A missing nutrient has no row (unknown ≠ 0) and shows up as "incomplete" in totals.
- Match foods in code, not with an LLM and not with SQL full-text: whole-word matching and a transparent score (see [nutrition.md](../architecture/nutrition.md)). The score is a heuristic,
  benchmarked against real descriptions in unit tests; ambiguity is reported and alternatives are always available. The LLM only extracts what the user said.
- Raw and cooked are separate states of a food; the matcher honours the user's wording, and states its assumption when it has to choose.

**Alternatives.** Live USDA API per request (key management, latency, availability, changing data); embeddings for food search (non-deterministic, harder to explain; may be added as a re-ranker with
the Phase 4 vector infrastructure); a hand-typed seed (would be unverifiable).

**Consequences.** The repository ships no food data: a fresh database has no foods until the import is run (documented in the README; the food search then returns nothing instead of inventing results).
SR Legacy is old and generic; Romanian foods need another source. Heuristic tuning will continue as real usage shows failures; each one should become a benchmark case.
