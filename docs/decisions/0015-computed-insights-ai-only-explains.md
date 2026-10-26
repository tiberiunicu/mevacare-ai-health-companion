# ADR-0015: Insights are computed on request by plain rules; the AI only explains them

**Status:** Accepted

**Context.** Phase 6 needs weight, nutrition and sleep trends, comparisons with earlier periods, combined insights and "AI-assisted explanations using verified data".
The data is health data of one person, usually few points and noisy; the product must not diagnose, must not present correlation as cause, and must not let a model invent numbers.

**Decision.**
- A dedicated **Insights module** reads through the existing `Measurements` and `Nutrition` contracts and computes everything **on request** with deterministic code (`InsightEngine`): least-squares trends per week, period-over-period comparisons, and a fixed catalogue of rules.
  **No tables**: nothing to migrate, nothing that can go stale, and the whole result is a pure function of the stored data (tested for determinism).
- Rules and thresholds are **explicit and documented**, each result carries the **numbers it rests on, a confidence label and caveats**, and weak data yields "insufficient data" instead of a guess. Combined insights are worded as associations; a test scans statements for causal language.
- **Provider scores are never blended**: every device metric is analysed and averaged per provider and labelled with it.
- The **AI never calculates**. For chat it gets a read tool (`GetHealthInsights`); for the "Explain" button it gets the computed insights as delimited data and strict rules, no tools, no history and no user text. Its output is checked afterwards: numbers that are not in the evidence it was given are reported to the user (`ungroundedNumbers`).
  The explanation is stateless (not stored) and optional: the deterministic statements, translated in the UI from their facts, are the complete product without any model.
- The UI phrases insights from facts via translation templates (English, Romanian); the server's English statement is the fallback for unknown codes and the text the model/tool consumers see.

**Alternatives.** A model that reads raw records and finds patterns (non-reproducible, can invent numbers and causes, expensive); persisting insights in tables with a nightly job (stale data, a second source of truth, retention and deletion questions, no benefit at this data size);
statistical significance tests or ML forecasting (false precision with 10–60 noisy points; harder to explain to the user); medical reference ranges (this is a wellness product — range claims would be clinical claims); putting the engine in the Assistant module (couples trend math to the LLM and makes the product unusable without a provider).

**Consequences.** Insights cost a few small queries per page view (a 90-day window reads 180 days); caching can be added if it ever matters. Thresholds are product guesses that need tuning on real data. Wording lives in two places (server English statements, UI templates):
a new rule needs both, and the UI falls back gracefully if one is missing. The explanation quality, the model's compliance with the number rules and Romanian output are unverified until a real provider is configured.
