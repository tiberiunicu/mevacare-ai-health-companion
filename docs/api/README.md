# HTTP API

The live, machine-readable contract is served by the API in Development: `GET /openapi/v1.json` (OpenAPI 3.1) and the
reference UI at `/scalar`. All routes are under `/api` and require a bearer token unless marked *anonymous*.
Errors are RFC 9457 problem documents with an extra `code` (stable, for programs) and, for validation errors, `errors`.

| Method and path | Purpose |
|---|---|
| `POST /api/auth/register` *anonymous, rate limited* | create account (+ initial consents); sets the refresh cookie |
| `POST /api/auth/login` *anonymous, rate limited* | sign in; sets the refresh cookie |
| `POST /api/auth/refresh` *anonymous, rate limited* | new access token from the httpOnly cookie (rotates it) |
| `POST /api/auth/logout` *anonymous* | revoke the refresh-token family, clear the cookie |
| `GET /api/auth/me` | current user |
| `GET /api/consents` · `PUT /api/consents/{type}` `{granted}` | list / change consent (`TermsOfService` cannot be withdrawn) |
| `GET /api/account/export` | GDPR export (JSON) |
| `POST /api/account/delete` `{password}` | GDPR erasure |
| `GET/PUT /api/health-profile` | profile (needs `HealthDataProcessing`) |
| `GET/POST /api/health-goals` · `DELETE /api/health-goals/{id}` | goals, max 5 (needs `HealthDataProcessing`) |
| `GET/PUT /api/health-preferences` | language, units, theme (no consent needed) |
| `GET /api/nutrition/foods/search?q=&preparation=&limit=` | ranked foods with nutrients per 100 g (public food data, any signed-in user); packaged products also carry `brand`, `barcode` and `energyDerived` (calories calculated because the source has none; meal items from such foods are estimates); a barcode as the whole query finds that product |
| `GET /api/nutrition/foods/online/search?q=` | preview of packaged products in Open Food Facts (`barcode, name, brand, category, quantity, per100g, sourceLabel, foodId, energyDerived`); nothing is stored; needs the `ExternalIntegrations` consent; `400 invalid_query`, `403 consent_required`, `429 rate_limited` (server-wide limit), `503 food_online_unavailable \| food_online_disabled` |
| `POST /api/nutrition/foods/online/add` `{barcode}` | stores that product (the server re-reads it by barcode; values from the client are never accepted) and returns it like a search result; idempotent; `400 invalid_barcode \| product_data_unusable`, `404` (not found online), plus the errors above |
| `GET /api/nutrition/summary/daily?date=` · `GET /api/nutrition/summary/range?from=&to=` | totals, targets, remaining, meals (needs `HealthDataProcessing`; range ≤ 366 days) |
| `GET/PUT /api/nutrition/targets` | the user's own daily targets (every value optional) |
| `POST /api/nutrition/meals` · `GET/PUT/DELETE /api/nutrition/meals/{id}` | meals; items carry a food id and grams (server calculates nutrients) or a custom estimate; `proposalId` confirms an assistant proposal (201 created, 200 if it was already created) |
| `GET /api/conversations/{id}/proposals` · `POST /api/assistant/proposals/{id}/dismiss` | meals prepared by the assistant (state Proposed, Committed or Dismissed) |
| `POST /api/chat/stream` | send a message, stream the answer (needs `AiProcessing`; per-user rate limit) |
| `GET /api/conversations` · `GET /api/conversations/{id}/messages` · `DELETE /api/conversations/{id}` | chat history |
| `POST /api/measurements/weight` `{kilograms, measuredAt?}` · `GET /api/measurements/weight?from=&to=` · `DELETE /api/measurements/weight/{id}` | manual weight entries (kg, 20–500) and weight history with latest value and change; imported values cannot be deleted here (needs `HealthDataProcessing`; ranges ≤ 366 days) |
| `GET /api/measurements/sleep-recovery?from=&to=` | per-day, **per-provider** night (longest non-nap sleep ending that local day), naps, recovery score / resting heart rate / HRV, and per-provider averages |
| `GET /api/measurements/activity?from=&to=` | per-day, per-provider day strain, energy burned and workouts |
| `GET /api/integrations` | the available providers with `configured`, `connected`, `status` (`Connected`, `NeedsReauthorization`), last synchronization |
| `POST /api/integrations/{provider}/connect` | starts the OAuth flow: `{authorizationUrl}` to send the browser to (needs `ExternalIntegrations` + `HealthDataProcessing`; `503 integration_not_configured` without credentials) |
| `GET /api/integrations/{provider}/callback` *anonymous, rate limited* | the provider's redirect target; consumes the single-use `state` and **always answers with a redirect** to the web app: `/devices?connected=whoop` or `/devices?error=<code>` |
| `POST /api/integrations/{provider}/sync` · `GET /api/integrations/{provider}/sync-jobs?limit=` | synchronize now (one per minute per account) and recent runs |
| `DELETE /api/integrations/{provider}?deleteData=false` | disconnect (revokes at the provider, deletes the stored tokens; `deleteData=true` also removes the imported history) |
| `GET /api/insights?days=30&to=YYYY-MM-DD` | trends (per week, with confidence), comparison with the previous period of the same length, combined insights with facts and caveats, data coverage and limitations; `days` 7–90 (`400 invalid_period` otherwise); needs `HealthDataProcessing`; computed on request, nothing stored |
| `POST /api/assistant/insights/explain` `{days, to?, language?: en\|ro}` | AI explanation of those insights: `{explanation, ungroundedNumbers[], model, generatedAtUtc}`; needs `AiProcessing` + `HealthDataProcessing`; per-user chat rate limit; `400 nothing_to_explain` (no model call), `503 ai_not_configured\|ai_unavailable\|ai_timeout\|ai_empty_response\|ai_content_filtered` |
| `GET /api/knowledge/sources` | approved medical-knowledge sources behind the assistant's citations (title, publisher, URL, licence, retrieval date, version, passage count) |
| `GET /health/live` · `GET /health/ready` *anonymous* | liveness / readiness (database) |
| `GET /health/knowledge` *anonymous* | `{enabled, vectorStore, vectorStoreProvider, embeddingModel, activeSources}`; 503 when the vector store is unreachable; no secrets |

## Chat stream protocol

Request body: `{ "conversationId": "guid|null", "message": "…≤4000 chars", "clientMessageId": "guid|null", "clientTime": "ISO-8601 with offset|null" }` (`clientTime` lets the assistant know the user's local date).
Response: `200 text/event-stream`. Events (each `event: <name>` + `data: <json>`):

| Event | Data | Notes |
|---|---|---|
| `conversation` | `{conversationId, title}` | always first |
| `safety` | `{category}` | the following text is a fixed safety answer, not model output |
| `tool` | `{name, status}` | status `running`, `done` or `failed`; names: `GetHealthProfile`, `GetDailyNutritionSummary`, `GetNutritionSummary`, `PrepareMeal`, `SearchMedicalKnowledge`, `GetWeightHistory`, `GetSleepAndRecovery`, `GetHealthInsights` |
| `proposal` | `{card}` | a meal prepared by the assistant (`card.id`, `card.proposal` with matched items, grams, nutrients, totals, warnings and `localDate`, the day the user named or null for now); **nothing is saved** until `POST /api/nutrition/meals` with that `proposalId` |
| `delta` | `{text}` | zero or more |
| `citations` | `{items:[{number, sourceId, title, publisher, url, license, retrievedOn, excerpt, cited}]}` | only when the model searched the approved sources and found something; sent after the last `delta`, before `done`; `cited=false` means consulted but not referenced by the answer. The same list is returned as `citations` on each message of `GET /api/conversations/{id}/messages` |
| `done` | `{messageId}` | terminal, success |
| `error` | `{code, message, messageId}` | terminal; `ai_unavailable`, `ai_timeout`, `ai_empty_response`, `ai_content_filtered` (the provider's safety filter refused the message) |

Problems detected before streaming are normal HTTP errors: `400` validation, `401`, `403 consent_required`,
`404` (conversation not yours or missing), `409 duplicate_message`, `429` rate limit, `503 ai_not_configured`. Nutrition adds `400 unknown_food`,
`invalid_range`, `range_too_long`, `409 proposal_not_open` (dismissed) and `404` for a proposal that is not the caller's.
Integration codes: `integration_not_configured` (503), `sync_in_progress` (409), `429` for a too frequent manual synchronization, `404` when the provider is unknown or not connected; callback redirect errors are `invalid_state`, `authorization_denied`, `authorization_failed`, `invalid_request`, `not_configured`, `consent_required`, `no_refresh_token`, `provider_unavailable`, `account_already_linked`, `different_account`. A synchronization run reports `status` and, on failure, `errorCode` (`reauthorization_required`, `rate_limited`, `provider_unavailable`, `consent_required`, `internal_error`) in a normal 200 response.
Inside a turn, knowledge search failures do not break the chat: the tool result carries `knowledge_unavailable` (embedding provider or Qdrant down) or `knowledge_reindex_required` (the embedding model changed since indexing) and the assistant says it cannot consult its sources.
A repeated `clientMessageId` in the same conversation is rejected with `409` so client retries never duplicate a message.
