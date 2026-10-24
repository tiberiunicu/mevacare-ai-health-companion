# ADR-0013: Normalized measurements with provenance; provider scores stay provider-specific

**Status:** Accepted

**Context.** Health values arrive from the user (weight) and from wearables whose data models differ (sleep stages, recovery scores, strain).
The product must keep history for analysis, never mix incompatible numbers, never duplicate wearable records, and keep domain entities independent of any vendor.

**Decision.**
- A **Measurements** module owns the data; it knows no provider. Providers deliver provider-neutral records (`NormalizedMeasurement`, `NormalizedSleep`, `NormalizedWorkout`) through `IMeasurementIngest`.
- Structure follows the data's shape: single values in `HealthMeasurements` (metric enum, value, unit), and sessions with their own columns in `SleepSessions` and `Workouts` — not one wide key/value table.
- **Provenance**: every row belongs to a `MeasurementSource` (user + provider). Manual entries use the source `manual`. Rows can be listed, exported and deleted per provider.
- **No duplicates**: wearable rows carry the provider's record id; unique indexes on (source, metric, external id) / (source, external id) make imports idempotent. The same import is an update, so a record first seen as "pending score" becomes scored later.
- **Time**: UTC instant + the source's own UTC offset + the local date computed once at import. Daily views group by local date, so "the night of Tuesday" is stable whatever the server's zone.
- **Scores are metrics of their provider**, not generic quantities: `RecoveryScore` and `DayStrain` are always read and averaged per provider, and the UI and tools name the provider. HRV is stored as RMSSD explicitly. Physical quantities (weight, resting heart rate, energy) are comparable across sources, with their source shown.
- Unscored records keep identity and no values; implausible values are dropped individually rather than rejecting a whole page.

**Alternatives.** A generic key/value measurement table for everything (loses typed sessions, harder queries and constraints); storing provider JSON blobs (no analysis, vendor-coupled);
a "universal recovery score" mapped from each vendor (invents equivalence — the master requirements forbid treating device scores as equivalent without a documented mapping); using embeddings for history (relational data stays relational).

**Consequences.** Adding a provider means a mapper, not a schema change, unless it brings a new kind of data. Cross-provider analysis (Phase 6) must define explicit, documented mappings or stay per provider.
Several weight sources show side by side without a "preferred source" rule yet. Local-day assignment for provider cycles is an assumption that needs checking against the real service.
