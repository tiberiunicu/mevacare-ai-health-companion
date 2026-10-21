# ADR-0007: Modules and tables are added in the phase that needs them

**Status:** Accepted — a deliberate deviation from the brief

**Context.** The brief lists entities for Identity, Health Profile, Nutrition, Measurements, Assistant (including
`ToolExecutions`) and Integrations "initially", but also says: do not implement future phases prematurely, avoid unused
infrastructure, prefer a working vertical slice, and never create placeholder features that appear functional.

**Decision.** Phase 1 creates the Identity and HealthProfile tables; Phase 2 adds `Conversations` and `ChatMessages`; Phase 3 adds the Nutrition tables
(`Foods`, `FoodNutrients`, `FoodPortions`, `Meals`, `MealItems`, `NutritionTargets`) and `ToolExecutions`.
Measurements and Integrations tables and their module projects are created with the feature that uses them. Their design is documented in [overview.md](../architecture/overview.md#designed-not-yet-created).
Empty UI sections say "Not available yet" and name the phase; the dashboard never shows demonstration numbers.

**Consequences.** Smaller migrations and no dead tables; the data model for later modules can still change after
learning from earlier phases. The cost is that the complete database schema from the brief does not exist yet.
