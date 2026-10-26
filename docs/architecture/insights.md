# Health intelligence: trends, comparisons and insights (Phase 6)

The **Insights** module turns the data the other modules store into trends, period comparisons and a short list of observations.
It has **no tables and no model**: on each request it reads the caller's data through the `Measurements` and `Nutrition` contracts
(which check consent and filter by user), applies plain rules and returns the result. Nothing is persisted, so an insight can never be
stale or disagree with its source. Decision record: [ADR-0015](../decisions/0015-computed-insights-ai-only-explains.md).
The optional AI explanation on top is described in [insight-explanations.md](../ai/insight-explanations.md).

```
Measurements ─ IMeasurementsAssistantApi ─┐
Nutrition    ─ INutritionAssistantApi ────┼─► InsightsService ─► InsightEngine (pure, deterministic) ─► HealthInsightsDto
                                          │         (period = last N days, comparison = the N days before)
GET /api/insights?days=&to=  ◄────────────┤                                       │
assistant tool GetHealthInsights ◄────────┤                                       ├─► POST /api/assistant/insights/explain (LLM words the numbers)
Insights page, dashboard card ◄───────────┘                                       └─► statements + facts + caveats (no LLM needed)
```

## What is computed

**Period.** `days` is 7 to 90; `to` is the user's last local day (the client sends it, like the nutrition endpoints). The current
period is `[to − days + 1, to]` and the comparison period is the `days` before it. One read covers both.

**Trends** (per metric, per provider where the metric belongs to a device): least-squares slope of the daily values against the calendar,
expressed per week, with the number of days used, the time span and R². Several entries on one day count once. A trend needs at least
**3 days with data spread over 7 days or more**; otherwise it is reported as `insufficient_data` with a machine-readable note and no number is invented.

| Metric | Source | "Stable" when the slope is under |
|---|---|---|
| `weight_kg` | all weight entries, any source | 0.1 kg/week |
| `calories_kcal`, `protein_g` | day totals of logged days | 50 kcal/week, 3 g/week |
| `sleep_minutes` | time asleep of the night (light + deep + REM), per provider | 10 min/week |
| `recovery_score`, `hrv_ms`, `resting_heart_rate_bpm` | the provider's recovery, per provider | 2 points, 1 ms, 0.5 bpm per week |

**Confidence** is a rule, not a probability: *high* = 14+ days over 21+ days with R² ≥ 0.5; *low* = fewer than 5 days, a span under 10 days or R² under 0.15; *moderate* otherwise.

**Comparisons**: the average of the days with data in each period, the difference, the percentage, and a `meaningful` flag when the difference is larger than usual
day-to-day variation (0.3 kg, 100 kcal, 10 g, 20 min, 5 points, 4 ms, 2 bpm). Each period needs 3 days with data, otherwise `insufficient_data`.

These thresholds are product choices that decide what is worth mentioning. They are not clinical facts and are the first thing to tune with real users.

## Insights (rules)

Every insight has a stable `code`, a category, a severity (`info` or `notable`, never an alarm), numeric/text **facts**, **caveats** and a deterministic English `statement` built from the facts. The UI phrases insights from the facts in the user's language (English/Romanian).

| Code | Condition | Caveats it can carry |
|---|---|---|
| `weight_trend` | weight trend has enough data | `weight_varies_daily`, `low_confidence` |
| `weight_trend_shift` (combined, notable) | both periods have a weight trend and the pace **slowed** (≤ 50 % of a previous pace of ≥ 0.15 kg/week), **reversed**, or **accelerated** (≥ 150 %) | `association_not_cause`, `weight_varies_daily`, `low_confidence`, `logging_gaps` |
| `calories_vs_target`, `protein_vs_target` | a target is set and ≥ 5 logged days; *notable* when more than 15 % (calories) / 10 % (protein) off | `logging_gaps`, `incomplete_food_data` |
| `nutrition_logging_coverage` | window ≥ 14 days and meals logged on fewer than half of them | `logging_gaps` |
| `sleep_duration_change` | the comparison of time asleep is meaningful; *notable* when 30+ minutes shorter | — |
| `sleep_variability` | ≥ 7 nights and a standard deviation ≥ 90 minutes | — |
| `recovery_below_average` | the last three recovery scores are all ≥ 15 points below the user's own average (≥ 7 scores) | `provider_score` |
| `recovery_and_sleep` (combined) | ≥ 10 days with both; recovery differs ≥ 8 points between nights longer and shorter than the median | `association_not_cause`, `provider_score`, `small_sample` |
| `hrv_trend`, `resting_heart_rate_trend` | a stable-or-better trend direction with at least moderate confidence | `provider_score` |
| `data_gaps` | weight trend impossible, no meals logged, or no device data | — |

The weight shift is the answer to "why might my weight loss have slowed down?": it states the two paces and lists **what else changed meaningfully** in the same time
(average logged calories, average time asleep) as an association — or says that nothing else changed enough. It never states a cause.

Always-present **limitations** are returned with the result (`observational_only`, `not_medical_advice`, plus `provider_scores_not_comparable` and `logging_gaps` when they apply).

## Design rules that are enforced

- Provider scores (recovery, strain) are analysed per provider and named; averages and comparisons never blend providers (tested).
- A day without logged meals is *unknown*, not a zero-calorie day: nutrition averages use logged days, and coverage is reported.
- No statement uses causal wording (a test scans every generated statement); no medical thresholds or reference ranges are used.
- The engine is a pure function of its input (the same data gives byte-identical output); it never reads the clock or the database.
- Consent is checked by the contracts it reads through; users only ever see their own data.

## Limitations

- Linear trends over 7–90 days describe the past, not the future; short or noisy series give low-confidence results by design.
- Weight from several sources is combined without reconciliation (no "preferred scale").
- No seasonality, no menstrual-cycle awareness, no training-load model, no energy-balance estimate (it would need a reliable energy expenditure the application does not have).
- Correlation-style insights use a median split and need ≥ 10 days; they are descriptive and flagged as associations.
- Only English statements exist server-side; the UI translates from the facts (English and Romanian), and unknown codes fall back to the English statement.

## Tests

`tests/UnitTests/Insights` (statistics, every rule's conditions and boundaries, provider separation, wording, determinism) and
`tests/IntegrationTests/InsightsApiTests.cs` (the pipeline from stored weights, meals and device data to a slowdown insight with its factors, consent, isolation, periods, the assistant tool);
frontend tests render the page, comparison table, Romanian output, fallbacks and the dashboard card.
