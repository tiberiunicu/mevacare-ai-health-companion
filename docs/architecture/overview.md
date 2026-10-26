# Architecture overview

MevaCare is a **modular monolith**: one deployable ASP.NET Core application whose code is split into modules with
explicit boundaries, one SQL Server database, and a separate React single-page app. No microservices, no message
broker, no multi-agent system (see [ADR-0001](../decisions/0001-modular-monolith.md)).

## Projects and dependency rules

```
                Api (composition root, middleware, OpenAPI)
               /   |        \                \
   Modules.Identity  HealthProfile  Nutrition  Assistant  Knowledge  Measurements  Integrations  Insights   (feature modules)
          \             |  \                 |   \
           \            |   Identity (User)  |    Identity (User)
            \___________|____________________|____
                        Infrastructure (AppDbContext, interceptors, audit log, migrations)
                                    |
                                Contracts (shared kernel)
```

| Rule | Why |
|---|---|
| Modules depend on `Contracts` and `Infrastructure`, never on the `Api` project | the host composes, modules do not know the host |
| `Infrastructure` never references a module | no cycles; modules reach the database through `AppDbContext.Set<T>()` |
| A module may reference `Identity` **only for the `User` entity** (to declare the foreign key to `Users`) | database-level referential integrity and cascading deletion (GDPR erasure) |
| Modules do not call each other's services; shared needs go through `Contracts` interfaces (`IConsentService`, `IAuditLog`, `IUserDataExporter`, `ICurrentUser`, and for the AI tools `INutritionAssistantApi`, `IHealthProfileReader`, `IMealProposalGate`, for the medical-knowledge tool `IKnowledgeSearch`, and for measurements `IMeasurementIngest` (Integrations → Measurements) `IMeasurementsAssistantApi`, and `IInsightsAssistantApi` (Insights → Assistant)) | keeps boundaries enforceable and the modules extractable later |
| Entities are never returned by the API; every endpoint uses request/response records | no mass assignment, stable contracts |

Inside a module the code is organised by **feature** (`Features/Auth`, `Features/Consents`…) rather than by technical
layer. Endpoints are thin: bind, validate (FluentValidation filter), call a service, shape the result. Business rules
live in domain classes and application services. MediatR is deliberately not used: a plain service class per feature
is enough and easier to debug and to teach.

### How a module plugs in

`IModule` (in `Contracts`) has three members: register services, expose the assembly with its EF configurations, map
endpoints. `Api/ModuleCatalog.cs` lists the modules explicitly, so adding one is one line and nothing is discovered by
reflection at runtime.

## Persistence

- One `AppDbContext`. It applies `IEntityTypeConfiguration<T>` classes from `Infrastructure` and from every module
  assembly given by the host (`PersistenceModelOptions`). Tables live in per-module schemas.
- Migrations live in `Infrastructure/Persistence/Migrations`; the design-time host is the `Api` project.
- Conventions: UTC `datetime2` everywhere (values are tagged `Kind=Utc` on read so JSON carries a `Z`), GUID v7 keys
  generated in code (`ValueGeneratedNever`), `CreatedAtUtc`/`UpdatedAtUtc` stamped by `AuditableEntitiesInterceptor`,
  enums stored as strings, explicit max lengths, check constraints for value ranges, `decimal(18,4)` by default.
- **User isolation**: every user-owned row has a `UserId` foreign key (cascade delete); every query filters on the
  caller's id, which comes from `ICurrentUser` (the validated JWT `sub` claim) and from nowhere else.
- Audit trail: `core.AuditEvents` is append-only, keeps a pseudonymous `UserId` (no foreign key, so it survives account
  deletion) and never stores health data, e-mail addresses or tokens.

### Implemented tables

| Schema.Table | Purpose |
|---|---|
| `identity.Users`, `UserRoles` | accounts, PBKDF2 password hash, lockout counters, roles |
| `identity.RefreshTokens` | hashed rotating refresh tokens grouped in families |
| `identity.UserConsents` | current decision per consent type and policy version |
| `health_profile.HealthProfiles`, `HealthGoals`, `HealthPreferences` | profile (1:1 with user), goals (≤ 5), display preferences (1:1) |
| `assistant.Conversations`, `ChatMessages` | chat history; messages ordered by a per-conversation `Sequence`; unique `ClientMessageId` makes retried sends idempotent |
| `assistant.ToolExecutions` | audit of every tool call; for meals also the proposal awaiting confirmation (see [ADR-0009](../decisions/0009-tool-calling-and-confirmed-writes.md)) |
| `nutrition.Foods`, `FoodNutrients`, `FoodPortions` | imported USDA SR Legacy food data with provenance; nutrients per 100 g; unknown nutrients have no row |
| `nutrition.Meals`, `MealItems`, `NutritionTargets` | the journal (items keep a nutrient **snapshot**), the user's own targets; details in [nutrition.md](nutrition.md) |
| `knowledge.Sources`, `knowledge.Chunks` | approved reference documents (provenance, licence, content hash, version, status, embedding model) and their text chunks; the vector index in Qdrant is derived from them and can be rebuilt |
| `measurements.MeasurementSources`, `HealthMeasurements`, `SleepSessions`, `Workouts` | normalized measurements with provenance, source time zone and local date; unique per provider record |
| `integrations.ConnectedAccounts`, `SyncJobs`, `OAuthStates` | provider links with encrypted tokens, synchronization history, pending single-use OAuth states; details in [measurements-and-wearables.md](measurements-and-wearables.md) |
| `core.AuditEvents` | security audit log |

All planned tables now exist; later phases add tables only when they need them (see [ADR-0007](../decisions/0007-incremental-modules-and-migrations.md)).

## Request flows

**Authenticated API call.** Browser → (Vite proxy in development) → Kestrel → exception handler → request logging →
security headers → CORS → rate limiter → JWT authentication → fallback authorization policy (every endpoint requires
a signed-in user unless it is explicitly `AllowAnonymous`) → endpoint → validation filter → feature service →
`AppDbContext`.

**Chat turn** (details in [LLM integration](../ai/llm-integration.md)): validate → require AI consent → load/create the
conversation owned by the caller → run the deterministic emergency screen → persist the user message → stream the
model answer as Server-Sent Events (the model may call the per-turn tools, see [tool calling](../ai/tool-calling.md)) →
persist the assistant message (also when it fails or the user disconnects).

**Connecting and synchronizing a wearable** (Phase 5): authenticated `connect` → provider authorization page → anonymous callback (single-use state) → protected tokens stored → the SPA triggers a synchronization → provider data is normalized and ingested idempotently → pages and assistant tools read it back. See [measurements-and-wearables.md](measurements-and-wearables.md).

**Insights** (Phase 6): `GET /api/insights` or the assistant tool → `InsightsService` reads weights and sleep/recovery through `IMeasurementsAssistantApi` and meals through `INutritionAssistantApi` → the pure `InsightEngine` returns trends, comparisons and rule-based insights; nothing is stored. The optional AI explanation puts the computed result into words and is checked for invented numbers. See [insights.md](insights.md) and [../ai/insight-explanations.md](../ai/insight-explanations.md).

**Answering from approved medical sources** (Phase 4): the model calls `SearchMedicalKnowledge` → `IKnowledgeSearch` embeds the query → Qdrant returns chunk ids → the chunks are loaded from SQL (active sources only) → numbered passages go back to the model → the answer cites `[n]` → a `citations` event and the stored message carry the sources. Details: [RAG](../ai/rag-embeddings-memory.md).

**Logging a meal with the assistant** (Phase 3): `PrepareMeal` → structured extraction → deterministic food match and
calculation → proposal card → the user confirms (`POST /api/nutrition/meals` with `proposalId`) → meal + committed proposal in
one transaction → journal and summaries.

## Error handling and logging

- `GlobalExceptionHandler` converts exceptions to RFC 9457 problem documents. `AppException` subclasses are expected
  failures with a stable machine-readable `code`; everything else becomes a generic 500 with a trace id.
- Serilog structured logging. Request logs contain method, path (no query string) and status. Health data, tokens,
  passwords, chat content and provider error bodies are never logged.
- `/health/live` (process) and `/health/ready` (database) are anonymous.

## Frontend

React 19 + TypeScript + Vite + Tailwind CSS 4, React Router, TanStack Query for server state, Zustand for the two small
pieces of client state (session, theme), react-i18next (English/Romanian, parity checked by a test), react-markdown
(no raw HTML) for assistant messages. The access token lives **in memory only**; the refresh token is an httpOnly,
SameSite=Strict cookie scoped to `/api/auth`, so a page reload restores the session without exposing a long-lived
credential to JavaScript. System fonts only, so a health app does not leak page views to a font CDN.
