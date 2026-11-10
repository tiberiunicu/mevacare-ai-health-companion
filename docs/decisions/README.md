# Architecture decision records

| # | Decision | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith with Clean Architecture boundaries | Accepted |
| [0002](0002-single-dbcontext-schema-per-module.md) | One `AppDbContext`, one SQL schema per module | Accepted |
| [0003](0003-authentication-jwt-refresh-cookie.md) | Own authentication: PBKDF2 + short JWT + rotating httpOnly refresh cookie | Accepted |
| [0004](0004-consent-and-gdpr-model.md) | Consent gates data access; export/delete through module contracts | Accepted |
| [0005](0005-microsoft-extensions-ai.md) | `IChatClient` (Microsoft.Extensions.AI) with OpenAI and Ollama providers | Accepted |
| [0006](0006-deterministic-safety-screen.md) | Deterministic emergency screen before the model | Accepted |
| [0007](0007-incremental-modules-and-migrations.md) | Modules and tables are added in the phase that needs them | Accepted |
| [0008](0008-frontend-token-handling-and-stack.md) | SPA stack and in-memory access token | Accepted |
| [0009](0009-tool-calling-and-confirmed-writes.md) | Per-turn tools bound to the user; AI prepares, the user confirms | Accepted |
| [0010](0010-food-data-source-and-matching.md) | USDA SR Legacy imported locally; transparent matching heuristic | Accepted |
| [0011](0011-rag-architecture-and-governed-ingestion.md) | RAG: SQL as system of record, derived vector index, operator-governed ingestion, honest citations | Accepted |
| [0012](0012-embedding-providers-and-lexical-baseline.md) | Pluggable embedding providers, offline lexical baseline, evaluation-based calibration | Accepted |
| [0013](0013-normalized-measurements-with-provenance.md) | Normalized measurements with provenance; provider scores stay provider-specific | Accepted |
| [0014](0014-connected-accounts-oauth-and-sync.md) | Connected accounts: OAuth with single-use state, protected tokens, in-process idempotent sync | Accepted |
| [0015](0015-computed-insights-ai-only-explains.md) | Insights computed on request by plain rules; the AI only explains them, with a number check | Accepted |
| [0016](0016-open-food-facts-products.md) | Packaged products from Open Food Facts: bulk import by country plus an explicit online lookup | Accepted |
| [0017](0017-chat-first-actions.md) | Chat-first actions: one assistant panel on every page; the user still saves with one tap | Accepted (meals built, other actions planned) |
