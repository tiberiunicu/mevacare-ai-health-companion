# What remains to be done

All six planned phases are implemented and tested. This page lists, honestly, what is **not** done or **not verified**, in the order I would tackle it.
*Effort*: S = hours, M = a day or two, L = a week or more. *Needs you* = something only the owner can provide (an account, a key, a decision, a legal text).

## 1. Make the AI real (chat is verified; embeddings and the evaluations below are not)

Chat, tool calling, streaming, meal extraction and insight explanations were verified against **Azure OpenAI / Foundry (`gpt-6-luna`)** through the zero-code OpenAI route (items 1.1 and 1.2 are done, see below). Everything else AI-related is still tested only with a **scripted model** or a throw-away mock.

| # | Task | Effort | Needs you |
|---|---|---|---|
| 1.1 | **Done (chat): real chat provider.** Azure OpenAI / Foundry works with the OpenAI provider (`scripts/setup-ai.ps1`; endpoint `…services.ai.azure.com/openai/v1/`, model = deployment name). Reasoning models need `Ai:OpenAI:ReasoningEffort=none` for function tools and `Ai:SendTemperature=false`. **Still open:** deploy `text-embedding-3-small` and use it for embeddings (item 1.3). Ollama is still unusable on the owner's PC until the NVIDIA driver (546.30) is updated. A dedicated Azure provider (Entra ID instead of a key) is not needed for now. | S | an embedding deployment |
| 1.2 | **Done: live evaluations run** (meal extraction 6/6; adversarial 10/10 answered cases pass, 2 refused by Azure's content filter). Results and caveats in [ai/ai-evaluation.md](ai/ai-evaluation.md). Re-run after any prompt or model change; grow the datasets. | S | — |
| 1.3 | **Real embeddings: partly done.** `text-embedding-3-small` (Azure) is deployed and the retrieval evaluation is calibrated for it (`MinScore 0.30`: real-corpus recall@3 100 % vs 83 % lexical, 0 false positives; see [ai/ai-evaluation.md](ai/ai-evaluation.md)). Verified end to end against a real Qdrant (binary v1.20.0): the 7 Qdrant integration tests pass and the API chat answers with citations. **Still open:** larger and Romanian question sets, real approved documents (only 5 sample pages are ingested), running Qdrant as a service with persistence/backup. | S | real documents |
| 1.4 | **Tool-selection evaluation** (not written): does the model call `PrepareMeal` for "I ate…", `GetHealthInsights` for "why did my weight slow down", `GetSleepAndRecovery` for "how did I sleep", the knowledge tool for "what is HRV", and none of them for small talk? Add a labelled dataset and a live test. | M | 1.1 |
| 1.5 | **Insight-explanation evaluation** (not written): on fixed synthetic inputs the explanation must stay inside the data, avoid causal wording and diagnosis, keep `ungroundedNumbers` empty and be usable in Romanian. See [ai/insight-explanations.md](ai/insight-explanations.md). | S–M | 1.1 |
| 1.6 | Check **small local models** (tool calling is unreliable on some); decide the supported minimum and document it. Verify the SSE streaming, tool status events and structured output against the real provider, not only the mock. | M | 1.1 |
| 1.7 | **Romanian quality**: system prompt, safety answers, explanations and meal parsing in Romanian have only been reviewed by reading. | S | a Romanian-speaking reviewer |
| 1.8 | **AI memory** (conversation summaries, user preferences such as "vegetarian") is designed in [ai/rag-embeddings-memory.md](ai/rag-embeddings-memory.md) and **not built**. | L | a product decision |
| 1.9 | Output moderation / PII filter on model answers is not implemented (documented in `ai-safety.md`). | M | — |
| 1.10 | Expand the emergency-screen dataset and languages; have a clinician review the fixed safety texts and the product's wellness wording. | M | a clinical reviewer |

## 2. Make WHOOP real (written from public documentation, never run against WHOOP)

| # | Task | Effort | Needs you |
|---|---|---|---|
| 2.1 | Register a WHOOP developer app, register the redirect URL, set `Integrations:Whoop:ClientId/ClientSecret` ([integrations/whoop.md](integrations/whoop.md)); connect a real member account; import and compare with the WHOOP app. | S–M | WHOOP developer account + a WHOOP member |
| 2.2 | Resolve the **unverified assumptions** one by one (form vs JSON token request, client authentication `Post` vs `Basic`, whether an `http://localhost` redirect is accepted, refresh-token lifetime, which local date a cycle belongs to, rate-limit headers). Each has a one-line fix listed in the guide. | S | 2.1 |
| 2.3 | Add the data the application ignores today if wanted: WHOOP body measurement (weight/height), SpO₂, skin temperature, step count of the cycle, zone durations. | M | — |
| 2.4 | **Webhooks** instead of hourly polling (needs a public HTTPS URL, signature verification is documented by WHOOP). | M | a public URL |
| 2.5 | Other providers (the abstraction exists): Garmin, Oura, smart scales, Google Health Connect. **Apple Health needs a native iOS app** (HealthKit) — it cannot be done from the .NET backend. | L each | accounts/devices; a mobile app decision |
| 2.6 | A "preferred source" rule for weights when several sources report (today they are shown side by side). | S | a product decision |

## 3. Before any real person's data (security, privacy, legal)

The detailed list lives in [architecture/security-and-privacy.md](architecture/security-and-privacy.md#not-yet-done). In priority order:

1. **Legal**: Terms of Service and Privacy Policy texts (the consent screen references placeholders), a DPIA, processor agreements with the AI provider (and region / zero-retention choice) and with any hosting provider. *Needs you / a lawyer.*
2. **Account security**: e-mail verification, password reset, multi-factor authentication (registration currently reveals whether an e-mail exists — a deliberate interim trade-off). M–L
3. **Transport and browser security**: HTTPS termination and a reverse-proxy configuration (forwarded headers, SSE buffering off, only `/api` and the API's own `/health/live|ready|knowledge` proxied), Content-Security-Policy for the SPA host. M
4. **Secrets and keys in production**: a real secret store; a persistent, protected, backed-up **Data Protection key ring** (`Integrations:DataProtectionKeysPath`) — losing it forces every wearable to be reconnected; database encryption at rest; key rotation. M
5. **Retention and backups**: jobs for expired refresh tokens and old audit events; backup/restore with matching retention; verify GDPR deletion against backups. M
6. **Admin tooling and role-based authorization** (roles exist in the token, no admin endpoint does) — needed to operate the knowledge corpus without a shell. M
7. Penetration test, dependency vulnerability monitoring in CI, a review of rate-limit values and of the audit log content. M
8. **Food search at scale**: ~1.05 million Open Food Facts products are imported for eight countries and the substring search takes ~0.5 s. Add a real index (SQL Server full-text or a dedicated search index) before more countries or several concurrent users. S–M
9. **Consent wording and policy version**: the *External integrations* text was extended to cover the online food lookup (Open Food Facts) without bumping `ConsentPolicy.CurrentVersion`; bump it (which asks everyone to consent again) and have the wording reviewed before real users. Also decide whether to keep the ODbL attribution/share-alike obligations in mind if the food table is ever published ([ADR-0016](decisions/0016-open-food-facts-products.md)). S

## 4. Operating it (nothing below exists yet)

| Task | Effort |
|---|---|
| CI pipeline (build, the three test projects, frontend lint/test/build, migration check `has-pending-model-changes`) | S–M |
| Dockerfiles for the API and the web app, a production compose/Helm setup; `docker-compose.yml` has **never been run** (Docker was not installed) | M |
| Observability: metrics, tracing, alerts on sync failure rates and AI error rates (logs exist and avoid sensitive data) | M |
| Run the background synchronization on **one** instance, or move it to a worker with a distributed lock before scaling out | S–M |
| Qdrant in production: authentication (`Knowledge:VectorStore:ApiKey`), TLS, backups, or switch to SQL Server vector search behind `IVectorStore` | M |
| Performance check of insights (a 90-day window reads 180 days of three sources) and caching if it ever matters | S |
| API versioning policy; OpenAPI is published in development only | S |
| Frontend end-to-end tests (Playwright), an accessibility audit (axe, keyboard, screen-reader pass on charts), more languages | M |

## 5. Knowledge base (RAG) content

- **No medical document is shipped.** An operator must choose, license-check and ingest a corpus. The five sample pages in the developer's database are a **demo** whose licences are not reviewed ([ai/rag-embeddings-memory.md](ai/rag-embeddings-memory.md)). *Needs you.*
- PDF and table extraction are not supported; non-English sources need extraction and evaluation work (the corpus is English; the UI is English/Romanian).
- Keep the retrieval gold set growing with real questions; re-run it whenever the corpus or the embedding model changes.

## 6. Product ideas deliberately left out

Photo-based food recognition (postponed by the brief), barcode scanning with the camera (typing a barcode already works), traditional Romanian dishes without a barcode (packaged products come from Open Food Facts since ADR-0016), recipes and meal plans, weight-goal progress using the stored target weight/date,
reminders and notifications, workout planning, body-composition metrics, data export in other formats, offline/PWA mode, a mobile app.

## 7. Known small issues and technical debt

- The request logger labels some handled 4xx responses (for example the 401 of `/api/auth/refresh` without a cookie) as `responded 500` at error level, although the client receives the correct 401 — noisy and misleading logs; fix the order of the logging and exception-handling middleware.
- Insight thresholds (what counts as a stable weight, a meaningful sleep change, a "notable" deviation) are product guesses; tune them with real users' data ([architecture/insights.md](architecture/insights.md)).
- Statement wording exists in two places (server English statements and UI templates); a new insight rule needs both.
- Git reports CRLF→LF normalisation warnings on Windows for some files; adding a `.gitattributes` would silence them and make line endings explicit.
- On the owner's machine the user environment variable `CUDA_VISIBLE_DEVICES=-1` (set while trying Ollama) is still present; remove it after updating the NVIDIA driver.

## What is solid today

Authentication and consent model, GDPR export/deletion, the nutrition journal and calculations, the measurement and insight engines (deterministic, unit-tested), the OAuth/synchronization machinery (idempotent, tested against a scripted provider),
the RAG pipeline against a real Qdrant, the safety screen, and the React app (light/dark, English/Romanian). Test counts at the end of Phase 6 are recorded in the README.
