# Measurements and wearable integrations (Phase 5)

Two modules with a one-way relationship: **Integrations** talks to providers and hands provider-neutral records to **Measurements**,
which owns the health data. Neither knows the other's types; they meet in `Contracts/Measurements`
(`NormalizedHealthData`, `IMeasurementIngest`) — see [ADR-0013](../decisions/0013-normalized-measurements-with-provenance.md) and
[ADR-0014](../decisions/0014-connected-accounts-oauth-and-sync.md). The WHOOP specifics are in [whoop.md](../integrations/whoop.md).

```
 browser ──► /api/integrations/{provider}/connect ──► provider authorization page ──► /callback (anonymous, single-use state)
                                                                                        │ tokens encrypted (Data Protection)
 sync (button, hourly worker) ──► SyncService ──► IWearableProvider.FetchAsync ──► NormalizedHealthData batches
                                       │                                             │
                                       └─ job record, failure code                   ▼
                                                                         IMeasurementIngest.UpsertAsync (idempotent)
                                                                                        │
 browser ◄── /api/measurements/{weight|sleep-recovery|activity} ◄── per-provider day model ◄── measurements.* tables
 assistant tools GetWeightHistory / GetSleepAndRecovery ───────────────────────────────────┘ (caller's own data only)
```

## Tables

| Schema.Table | Purpose |
|---|---|
| `measurements.MeasurementSources` | one row per user and provider (`manual`, `whoop`, …): the provenance every value points to |
| `measurements.HealthMeasurements` | single values: `Weight`, `RestingHeartRate`, `HrvRmssd`, `RecoveryScore`, `DayStrain`, `CaloriesBurned`; value, unit, UTC instant, source offset, local date, provider record id. **Unique (source, metric, external id)** where the id exists |
| `measurements.SleepSessions` | start/end, offset, local date (of the end), nap flag, scored flag, stage minutes, performance/efficiency/consistency %, respiratory rate, disturbances. Unique (source, external id) |
| `measurements.Workouts` | sport, times, offset, local date, strain, heart rates, kcal, distance. Unique (source, external id) |
| `integrations.ConnectedAccounts` | user, provider, provider account id, status, scopes, **protected** access/refresh tokens, expiry, last sync/attempt/status. Unique (user, provider) and (provider, provider account id) |
| `integrations.SyncJobs` | one row per run: trigger, status, window, counts, stable error code (30 kept per account) |
| `integrations.OAuthStates` | hash of the single-use `state` of a pending connect, its user and expiry |

The measurement tables carry `UserId` as a plain column (SQL Server allows one cascade path); rows are deleted through their source,
which cascades from the user, so account deletion removes everything.

## Rules that are enforced in code

| Rule | Where |
|---|---|
| No fabricated data: pages render only what is stored; empty states say what is missing and how to get it | `HealthPages.tsx`, dashboard cards |
| Provenance on every value | `MeasurementSource`; DTOs carry the provider |
| Idempotent import; overlapping windows update in place | unique indexes + `MeasurementIngestService` |
| Device scores are not equivalent across devices | scores are separate metrics; day rows and averages are **per provider**; UI labels carry the provider name |
| Health data needs consent | every read/write calls `EnsureGrantedAsync(HealthDataProcessing)`; connecting and syncing also need `ExternalIntegrations` |
| Local day is preserved | UTC + source offset + local date, assigned once at import |
| Tokens never leak | protected at rest; absent from responses, logs, export |
| One provider account ↔ one user | unique index + `account_already_linked`; switching accounts needs a disconnect (`different_account`) |
| User isolation | all queries filter by the authenticated user id; callback identifies the user only through the single-use state |

## The day model

For one provider and one local date: the **night** is the longest non-nap sleep that ended that day; naps are summed separately;
recovery values are those dated that day; strain/energy are the cycle(s) that started that day; workouts are those that started that day.
Averages (sleep, recovery, resting heart rate, HRV) are computed per provider over the days that have a value, with the number of days stated.

## Synchronization

1. Take the per-account lock (a second run gets `409 sync_in_progress`); a manual run is throttled to one per minute.
2. Skip with `consent_required` when a consent is off; fail with `reauthorization_required` without calling the provider if the account is flagged.
3. Window: first run 30 days back; later runs from *last success − 7 days*, so late-scored records are picked up.
4. Refresh the access token when it expires within 2 minutes (and persist the rotated pair immediately), or once after a 401.
5. Fetch sleep → recovery → cycles → workouts page by page; ingest each batch as it arrives (progress survives a later failure).
6. Record the job; on failure record a stable code and retry at the next cycle. Success moves the "last successful sync" marker.

## Assistant tools

`GetWeightHistory(from, to)` and `GetSleepAndRecovery(from, to)` are read-only, bound to the caller, return at most the last 60/31 days plus
averages, and tell the model that scores belong to the provider and are not to be turned into medical conclusions
([tool-calling.md](../ai/tool-calling.md)).

## Limitations

- Only WHOOP, and not yet verified against the real service. Apple Health needs a native iOS app (HealthKit); Garmin, Oura, Google Health Connect are not started.
- No webhooks, no intraday data, no sleep-stage timeline, no steps; recovery/strain are never "explained" until Phase 6.
- Weights from different sources are shown together without reconciliation (no "preferred source").
- The background worker is in-process (single instance) and has no distributed lock.

## Tests

`tests/UnitTests/Integrations` (mapper rules, provider HTTP behaviour with a stub handler, token protection),
`tests/IntegrationTests/WearableTests.cs` (the full OAuth and sync lifecycle against the scripted WHOOP: state security, protected tokens, rotation,
idempotency, partial progress, failure codes, consent, isolation, disconnect, export/deletion) and `MeasurementsApiTests.cs` (weights, validation,
day model, provider separation, assistant tools), plus frontend tests for the pages, dashboard cards and device management.
