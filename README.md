# MevaCare AI Health Companion

An AI-powered personal health companion: AI chat as the main interface, with traditional screens for the
dashboard, nutrition, measurements, profile and privacy. It gives **wellness information only** — it does not
diagnose, prescribe or replace a physician, and it is not an emergency service.

This is a brand-new, standalone product. It shares nothing (code, database, infrastructure) with any other MevaCare
application.

> **Status: Phase 1 (foundation), Phase 2 (AI chat), Phase 3 (nutrition), Phase 4 (medical knowledge RAG), Phase 5 (measurements and WHOOP wearable integration) and Phase 6 (health intelligence) are implemented and tested — **all six planned phases are done**. What is *not* verified or not built is listed in [docs/remaining-work.md](docs/remaining-work.md): above all, nothing has run against a real AI model or the real WHOOP service.**
> See [What works today](#what-works-today) and [Roadmap](#roadmap).

## What works today

| Area | Status |
|---|---|
| Modular monolith, .NET 10, EF Core 10 (SQL Server), OpenAPI, Serilog, health checks, rate limiting | Done |
| Accounts: register, sign in (JWT access token + rotating httpOnly refresh cookie), lockout, sign out | Done |
| Consent model (terms, health data, AI processing, external integrations), audit log | Done |
| GDPR export and account deletion | Done |
| Health profile, goals, display preferences | Done |
| AI chat: `IChatClient` with OpenAI or Ollama, SSE streaming, persisted history, safety screen, rate limit | Done (see *AI provider testing* below) |
| Nutrition: USDA food data import, deterministic nutrient calculation, food journal, daily/range summaries, targets, custom (estimated) items | Done |
| AI meal logging: structured extraction → food match → proposal card → user confirmation → journal (tool calling) | Done (scripted-model tests and a mock OpenAI server; see *AI provider testing*) |
| Medical knowledge RAG: operator-run ingestion of approved, licensed documents → chunking → embeddings → Qdrant, grounded answers with numbered citations, retrieval evaluation | Done; **no documents are shipped**, you supply a corpus (see *Medical knowledge* below). Real OpenAI/Ollama embeddings never run |
| React app: sign in/up, dashboard (real calories and macros), profile, preferences, privacy, AI settings (incl. the approved sources list), chat with tool status, meal cards and source citations, nutrition pages; light/dark; English + Romanian | Done |
| Measurements: manual weight (kg/lb), weight evolution, sleep sessions, recovery, day strain, workouts, with provenance and local-day handling; historical queries; assistant tools `GetWeightHistory` and `GetSleepAndRecovery` | Done |
| Wearables: connected-account infrastructure, WHOOP OAuth, hourly + on-demand synchronization, normalization, sleep/recovery/activity pages and dashboard cards, connect/synchronize/disconnect UI | Done, **but never run against the real WHOOP service** (scripted stand-in only; see [docs/integrations/whoop.md](docs/integrations/whoop.md)) |
| Health intelligence: weight, nutrition, sleep and recovery trends, comparison with the previous period, combined insights (for example *why did my weight loss slow down*, as associations, never causes), computed on request by deterministic rules; insights page and dashboard card | Done |
| AI-assisted explanation of the insights (button on the insights page) and the chat tool `GetHealthInsights`; the model only words the computed numbers and its answer is checked for numbers that are not in the data | Done with a scripted model; **never run against a real model** |
| AI memory, other wearables (Apple Health, Garmin, Oura …) | **Not implemented.** See [docs/remaining-work.md](docs/remaining-work.md) |

**AI provider testing.** Automated tests use a scripted fake model. The OpenAI provider code path was additionally
exercised end-to-end (SDK → HTTP → streaming → browser, including **tool calls and JSON-schema structured output**) against a local OpenAI-compatible mock server. It has **not**
been run against the real OpenAI API or a real Ollama model, because no API key or Ollama installation was available
while building it. Run the [live evaluation](#tests) once you configure a provider.

## Repository layout

```
src/backend/
  MevaCare.AI.HealthCompanion.Api                  host: composition root, middleware, OpenAPI
  MevaCare.AI.HealthCompanion.Contracts            shared kernel: base entities, ICurrentUser, errors, validation filter
  MevaCare.AI.HealthCompanion.Infrastructure       AppDbContext, interceptors, audit log, EF migrations
  MevaCare.AI.HealthCompanion.Modules.Identity     users, auth, consents, GDPR export/delete
  MevaCare.AI.HealthCompanion.Modules.HealthProfile profile, goals, preferences
  MevaCare.AI.HealthCompanion.Modules.Nutrition    foods, meals, targets, summaries, USDA importer, meal proposals back end
  MevaCare.AI.HealthCompanion.Modules.Assistant    AI chat, providers, safety, conversations, tools, meal parsing
  MevaCare.AI.HealthCompanion.Modules.Knowledge    medical-knowledge RAG: ingestion, embeddings, vector store (Qdrant), retrieval
  MevaCare.AI.HealthCompanion.Modules.Measurements normalized measurements with provenance: weight, sleep, recovery, strain, workouts, queries
  MevaCare.AI.HealthCompanion.Modules.Integrations connected accounts, provider OAuth (WHOOP), protected tokens, synchronization
  MevaCare.AI.HealthCompanion.Modules.Insights     trends, period comparisons, rule-based insights (no tables, no model)
src/frontend/mevacare-ai-web                       React + TypeScript + Vite + Tailwind
tests/UnitTests | IntegrationTests | AIEvaluationTests
docs/architecture | decisions (ADRs) | ai | api
infrastructure/docker, docker-compose.yml, scripts/
```

Differences from the suggested structure (reasons in [ADR-0001](docs/decisions/0001-modular-monolith.md) and
[ADR-0007](docs/decisions/0007-incremental-modules-and-migrations.md)): each module project was added in the phase that implemented it,
together with its tables.

## Prerequisites

- .NET SDK 10 (`global.json` pins 10.0.x)
- Node.js 22+ and npm
- SQL Server reachable from your machine: a local instance (Developer/Express) **or** the Docker container below
- Optional: Docker (SQL Server + Qdrant), Ollama or an OpenAI API key (for the AI chat)

## Quick start (Windows, PowerShell)

```powershell
git clone <your-clone-url> mevacare-ai-health-companion
cd mevacare-ai-health-companion

# 1. Tools
dotnet tool restore                      # installs the pinned dotnet-ef

# 2. Secrets (stored outside the repo in dotnet user-secrets)
./scripts/setup-dev.ps1 -SqlServer 'localhost\SQLEXPRESS'   # use your own instance name; Windows auth by default

# 3. Database (creates MevaCareAIHealthCompanion from the migrations)
dotnet ef database update --project src/backend/MevaCare.AI.HealthCompanion.Infrastructure --startup-project src/backend/MevaCare.AI.HealthCompanion.Api

# 3b. Food data (once). Download the USDA FoodData Central "SR Legacy" CSV zip from https://fdc.nal.usda.gov/download-datasets,
#     extract it, then import it (public domain; takes ~15 s; safe to repeat). Without it the food search finds nothing.
dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api -- import-usda "C:\path\to\FoodData_Central_sr_legacy_food_csv_2018-04"

# 3c. Optional: packaged products with brands (Open Food Facts, ODbL licence, community data). Download
#     https://static.openfoodfacts.org/data/en.openfoodfacts.org.products.csv.gz (~0.8 GB, do not extract) and import the countries you want
#     (English names, lower case). Slow for many countries; safe to repeat. Without it, products can still be looked up online one by one (consent required).
dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api -- import-openfoodfacts "C:\path\to\en.openfoodfacts.org.products.csv.gz" romania,germany

# 4. API   -> http://localhost:5080  (OpenAPI: /openapi/v1.json, reference UI: /scalar)
dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api --launch-profile http

# 5. Web (second terminal)  -> http://localhost:5173
cd src/frontend/mevacare-ai-web
npm install
npm run dev
```

In Development the API also applies pending migrations on startup (`Database:MigrateOnStartup`), so step 3 is only
needed to create the database before the first run or from CI. The Vite dev server proxies `/api` to the API so the
refresh cookie works same-origin; set `VITE_API_PROXY_TARGET` if the API runs elsewhere.

Open http://localhost:5173, create an account, tick the consents you want (they can be changed later under
*Settings → Privacy & Consent*), and use *AI Companion* once an AI provider is configured.

### SQL Server with Docker instead

```powershell
Copy-Item .env.example .env        # then set MSSQL_SA_PASSWORD to a strong password of your choice
docker compose up -d sqlserver     # add Qdrant for the medical knowledge: docker compose up -d qdrant
./scripts/setup-dev.ps1 -SqlServer 'localhost,14330' -SqlUser sa -SqlPassword (Read-Host -AsSecureString)
```

`docker-compose.yml` was written but **not executed** in the authoring environment (Docker was not installed), so treat
it as unverified until you run it.

## Configuration

Configuration is layered: `appsettings.json` (safe defaults) → `appsettings.Development.json` → user-secrets
(Development) → environment variables (`Section__Key`). **Secrets are never committed.**

| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQL Server connection (required) |
| `Jwt:SigningKey` | HMAC key, ≥ 32 characters (required; the app refuses to start without it) |
| `Ai:Provider` | `Ollama` (default) or `OpenAI` |
| `Ai:Ollama:Endpoint` / `Ai:Ollama:Model` | local model, e.g. after `ollama pull <model>` |
| `Ai:OpenAI:ApiKey` / `Ai:OpenAI:Model` | OpenAI credentials and model (`Ai:OpenAI:Endpoint` optional, for compatible gateways) |
| `Ai:MaxOutputTokens`, `Ai:Temperature`, `Ai:HistoryMessageLimit`, `Ai:HistoryCharacterBudget`, `Ai:RequestTimeoutSeconds` | generation and cost limits |
| `Ai:ToolsEnabled`, `Ai:MaxToolIterations` | turn tool calling off (models without tool support) / cap model-tool round trips |
| `Knowledge:Enabled`, `Knowledge:Embeddings:*`, `Knowledge:VectorStore:*`, `Knowledge:Chunking:*`, `Knowledge:Retrieval:*` | medical-knowledge RAG; defaults work offline (Lexical embeddings, Qdrant on `localhost:6333`). See [docs/ai/rag-embeddings-memory.md](docs/ai/rag-embeddings-memory.md#configuration-knowledge) |
| `Integrations:Whoop:ClientId` / `ClientSecret` / `RedirectUri`, `Integrations:Sync:*`, `Integrations:DataProtectionKeysPath` | WHOOP connection and synchronization; without credentials the integration shows "not set up". See [docs/integrations/whoop.md](docs/integrations/whoop.md) |
| `Auth:Google:ClientId` | turns on **Sign in with Google** (public client id, no secret; empty = off). `./scripts/setup-google-signin.ps1`; see [docs/integrations/google-sign-in.md](docs/integrations/google-sign-in.md) |
| `Auth:*`, `RateLimiting:*`, `Cors:AllowedOrigins` | see `appsettings.json` |

Examples:

```powershell
$api = 'src/backend/MevaCare.AI.HealthCompanion.Api'

# Local model with Ollama (https://ollama.com): pick any chat model you have pulled
dotnet user-secrets set "Ai:Provider" "Ollama" --project $api
dotnet user-secrets set "Ai:Ollama:Model" "<your-pulled-model>" --project $api

# OpenAI
dotnet user-secrets set "Ai:Provider" "OpenAI" --project $api
dotnet user-secrets set "Ai:OpenAI:ApiKey" "<your key>" --project $api
dotnet user-secrets set "Ai:OpenAI:Model" "<model id>" --project $api

# Azure OpenAI / Microsoft Foundry (verified with a reasoning model): the script asks for the key with a hidden prompt and prints only setting names
./scripts/setup-ai.ps1 -Endpoint 'https://<resource>.services.ai.azure.com/openai/v1/' -Model '<deployment name>' -ReasoningEffort none -OmitTemperature
```

Settings for reasoning models (GPT-5 / GPT-6 families, o-series), all optional: `Ai:SendTemperature=false` (these models reject `temperature`),
`Ai:OpenAI:ReasoningEffort` = `none`\|`low`\|`medium`\|`high` (some refuse function tools unless it is `none`), `Ai:ReasoningTokenAllowance` (extra output tokens because
hidden reasoning counts against the limit), `Ai:OpenAI:Api` = `ChatCompletions`\|`Responses`, and `Ai:LogProviderErrorDetails=true` to log the provider's error text while debugging (off by default, it may contain request details).
The provider's own safety filter can refuse a message (HTTP 400 `content_filter`); the chat then answers with the code `ai_content_filtered` instead of an outage message.

**Windows note (Store/MSIX apps).** Some desktop apps and terminals see a *virtualized* `%APPDATA%`, so `dotnet user-secrets` may write to, or read, a different copy of `secrets.json`
than the API process started from a normal terminal. If the API says "not configured" although you stored the settings, run `dotnet user-secrets list --project $api` in the same
terminal that starts the API. `scripts/run-live-eval.ps1` therefore passes the settings to the test process as environment variables.

### Medical knowledge (RAG)

The assistant answers reference questions ("what is HRV?", "how much activity do adults need?") from **approved documents** and cites them. The repository contains **no medical documents**: you choose them, check their licence (the allow-list is public domain US-government works, CC0, CC-BY, CC-BY-SA) and ingest them on the server. Without any, the assistant says it has no approved source.

1. Start Qdrant: `docker compose up -d qdrant` (unverified compose file) or the official Windows binary (`qdrant-x86_64-pc-windows-msvc.zip` from the [qdrant/qdrant releases](https://github.com/qdrant/qdrant/releases); verified with v1.20.0): unzip it, set `$env:QDRANT__TELEMETRY_DISABLED = 'true'` (it reports telemetry by default) and run `qdrant.exe` from a folder where it may create `./storage` (REST on 6333, gRPC on 6334). `GET /health/knowledge` shows whether the API reaches it and which embedding model the index was built with.
2. Put the documents (`html`, `md`, `txt`; no PDF) and a `sources.json` with their provenance in a folder (format and example in [docs/ai/rag-embeddings-memory.md](docs/ai/rag-embeddings-memory.md#operating-it)).
3. Ingest: `dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api -- ingest-knowledge "C:\path\to\corpus"` (re-running is safe; changed content becomes a new version). Other commands: `reindex-knowledge [--recreate]` (after changing the embedding model) and `retire-knowledge <source-guid>`.
4. Ask the assistant a reference question; the sources appear under the answer and in *Settings → AI*.

The default embeddings are an **offline lexical baseline** (matches words, not meaning). For better retrieval set `Knowledge:Embeddings:Provider` to `OpenAI` or `Ollama` plus `Model` and `Dimensions`, run `reindex-knowledge --recreate`, and re-calibrate `Knowledge:Retrieval:MinScore` with the retrieval evaluation. With Azure OpenAI the same endpoint and key as the chat are used (`Ai:OpenAI:*`); only the embedding deployment name is new: `Knowledge:Embeddings:Provider=OpenAI`, `Knowledge:Embeddings:Model=<deployment, e.g. text-embedding-3-small>`, `Knowledge:Retrieval:MinScore=0.30` (calibrated for that model, see [docs/ai/ai-evaluation.md](docs/ai/ai-evaluation.md); the lexical default of 0.15 is too low for it). Retrieval was verified with these embeddings against the in-memory store; running them end to end against Qdrant is still open. The Ollama embedding provider was **not run** against a real service.

### Wearables (WHOOP)

Sleep, recovery, strain and workouts can be imported from a WHOOP account. You need a WHOOP developer app (client id and secret, registered redirect URL) and a WHOOP member account to try it; the steps, the exact commands and the list of **unverified assumptions** are in [docs/integrations/whoop.md](docs/integrations/whoop.md). In short: set `Integrations:Whoop:ClientId` and `ClientSecret` with user-secrets, turn on the *External integrations* consent, open *Connected devices* and press *Connect WHOOP*. Without the credentials the page says "Not set up on this server" and nothing else is affected. In production set `Integrations:DataProtectionKeysPath` to a persistent, protected folder (it holds the keys that encrypt the stored tokens) and run a single API instance (the hourly synchronization runs inside it).

Weight can always be entered by hand under *Health → Weight* (kg or lb, following your unit preference).

Without a configured provider everything except chat works; chat answers HTTP 503 `ai_not_configured` and the UI
shows "The AI service is not available right now".

## Database and migrations

Code-first EF Core; the single `AppDbContext` loads the entity configurations of every module and keeps each module's
tables in its own schema (`identity`, `health_profile`, `nutrition`, `assistant`, `knowledge`, `core`).

```powershell
$infra = 'src/backend/MevaCare.AI.HealthCompanion.Infrastructure'
$api   = 'src/backend/MevaCare.AI.HealthCompanion.Api'

dotnet ef migrations add <Name> --project $infra --startup-project $api --output-dir Persistence/Migrations
dotnet ef database update      --project $infra --startup-project $api
dotnet ef migrations remove    --project $infra --startup-project $api   # last, unapplied migration only
```

The design-time tools start the API host, so the connection string and `Jwt:SigningKey` must be configured.

## Tests

```powershell
dotnet test MevaCare.AI.HealthCompanion.slnx          # unit + integration + deterministic AI evaluation
dotnet test tests/UnitTests
dotnet test tests/IntegrationTests                     # needs SQL Server; creates and drops a throw-away database per run
dotnet test tests/AIEvaluationTests                    # emergency-screen and retrieval evaluation; live model eval is skipped by default
cd src/frontend/mevacare-ai-web; npm test; npm run build; npm run lint
```

- State after Sign in with Google ([ADR-0018](docs/decisions/0018-sign-in-with-google.md); earlier: chat-first actions, [ADR-0017](docs/decisions/0017-chat-first-actions.md)): 437 unit tests, 225 integration tests (+7 Qdrant tests skipped without `MEVACARE_QDRANT_URL`; they pass against Qdrant 1.20.0), 19 evaluation tests (+4 live-model tests skipped), 140 frontend tests; all pass. Google's own side was never exercised (stand-in only). The Open Food Facts tests use a scripted stand-in for the service; the real service (search and product lookup) was checked by hand once, through the UI.
- Integration tests boot the real API on a temporary database created **through the migrations**. The SQL Server comes
  from `MEVACARE_TEST_SQL` or, if unset, from the API's `ConnectionStrings:Default` user-secret (database name replaced).
- No test calls a paid or non-deterministic model: chat tests use a scripted `IChatClient`; knowledge tests use the offline Lexical embeddings and an in-memory vector store.
- Qdrant integration tests are skipped unless `MEVACARE_QDRANT_URL` is set: `$env:MEVACARE_QDRANT_URL='http://localhost:6333'; dotnet test tests/IntegrationTests` (they create and drop their own collections).
- Retrieval quality on your own corpus: `$env:MEVACARE_KNOWLEDGE_DIR='C:\path\to\corpus'; dotnet test tests/AIEvaluationTests --filter Retrieval` (needs a gold set shaped like `Datasets/retrieval-gold-demo-corpus.json`).
- Live model evaluation (real provider, adversarial prompts, costs money):
  `./scripts/run-live-eval.ps1` (uses the settings stored by `scripts/setup-ai.ps1`), or set `MEVACARE_EVAL_LIVE=1` and the `Ai__*` environment variables yourself and run `dotnet test tests/AIEvaluationTests`.
  See [docs/ai/ai-evaluation.md](docs/ai/ai-evaluation.md).
- Only synthetic data is used in tests and development. Do not enter real health data until the open items in
  [docs/architecture/security-and-privacy.md](docs/architecture/security-and-privacy.md#not-yet-done) are closed.

## Documentation

- [Architecture overview](docs/architecture/overview.md) · [Security and privacy](docs/architecture/security-and-privacy.md)
- [Architecture decision records](docs/decisions/)
- [Nutrition module](docs/architecture/nutrition.md) (data source and licence, rules, matching, limitations)
- [Measurements and wearables](docs/architecture/measurements-and-wearables.md) · [WHOOP setup and assumptions](docs/integrations/whoop.md) · [Sign in with Google](docs/integrations/google-sign-in.md) · [Deployment on the VPS (demo, live)](docs/deployment.md)
- [Insights (trends, comparisons, rules)](docs/architecture/insights.md) · [AI explanations of insights](docs/ai/insight-explanations.md)
- **[What remains to be done](docs/remaining-work.md)**
- AI: [index](docs/ai/README.md) · [LLM integration](docs/ai/llm-integration.md) · [tool calling](docs/ai/tool-calling.md) ·
  [structured outputs](docs/ai/structured-outputs.md) · [safety](docs/ai/ai-safety.md) · [evaluation](docs/ai/ai-evaluation.md) ·
  [RAG, citations and (planned) memory](docs/ai/rag-embeddings-memory.md)
- [HTTP API](docs/api/README.md)

## Roadmap

1. ✅ Foundation  2. ✅ AI chat  3. ✅ Nutrition vertical slice (food data, meal logging via tools and confirmation)
4. ✅ Medical knowledge RAG (Qdrant, citations)  5. ✅ Wearables (WHOOP first) and measurements  6. ✅ Health intelligence (trends, insights)

All planned phases are implemented; the next work is making the AI and WHOOP parts real and hardening for real users — see [docs/remaining-work.md](docs/remaining-work.md).

## Troubleshooting

- *"Connection string … is not configured" / "Jwt:SigningKey must be at least 32 characters"* — run `scripts/setup-dev.ps1`.
- *Login works but you are signed out on reload* — the refresh cookie is `Secure` unless `Auth:RefreshCookieSecure=false`
  (the Development settings do this). Behind HTTPS in production keep it `true`.
- *Chat says the AI service is unavailable* — configure a provider (above) and, for Ollama, make sure it is running.
- *Integration tests cannot connect* — set `MEVACARE_TEST_SQL` to a connection string that may create databases.
- *The assistant says it has no approved source* — nothing is ingested yet, the question is outside the corpus, or the lexical baseline did not match your wording; check `GET /health/knowledge` and *Settings → AI*.
- *"Not set up on this server" under Connected devices* — the WHOOP client id/secret are missing ([docs/integrations/whoop.md](docs/integrations/whoop.md)).
- *Connecting WHOOP returns "The connection attempt expired" or fails at the provider* — the redirect URL must match the one registered in the WHOOP dashboard exactly.
- *Behind a reverse proxy, `/health/weight` shows an API error* — proxy only `/api` and the API's own `/health/live|ready|knowledge`; every other `/health/...` path belongs to the web app (the Vite dev proxy does the same).
- *"knowledge_unavailable" / "knowledge_reindex_required"* — Qdrant or the embedding provider is down, or the embedding model changed: run `reindex-knowledge --recreate`.
