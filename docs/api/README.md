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
| `GET /api/nutrition/foods/search?q=&preparation=&limit=` | ranked foods with nutrients per 100 g (public food data, any signed-in user) |
| `GET /api/nutrition/summary/daily?date=` · `GET /api/nutrition/summary/range?from=&to=` | totals, targets, remaining, meals (needs `HealthDataProcessing`; range ≤ 366 days) |
| `GET/PUT /api/nutrition/targets` | the user's own daily targets (every value optional) |
| `POST /api/nutrition/meals` · `GET/PUT/DELETE /api/nutrition/meals/{id}` | meals; items carry a food id and grams (server calculates nutrients) or a custom estimate; `proposalId` confirms an assistant proposal (201 created, 200 if it was already created) |
| `GET /api/conversations/{id}/proposals` · `POST /api/assistant/proposals/{id}/dismiss` | meals prepared by the assistant (state Proposed, Committed or Dismissed) |
| `POST /api/chat/stream` | send a message, stream the answer (needs `AiProcessing`; per-user rate limit) |
| `GET /api/conversations` · `GET /api/conversations/{id}/messages` · `DELETE /api/conversations/{id}` | chat history |
| `GET /health/live` · `GET /health/ready` *anonymous* | liveness / readiness (database) |

## Chat stream protocol

Request body: `{ "conversationId": "guid|null", "message": "…≤4000 chars", "clientMessageId": "guid|null", "clientTime": "ISO-8601 with offset|null" }` (`clientTime` lets the assistant know the user's local date).
Response: `200 text/event-stream`. Events (each `event: <name>` + `data: <json>`):

| Event | Data | Notes |
|---|---|---|
| `conversation` | `{conversationId, title}` | always first |
| `safety` | `{category}` | the following text is a fixed safety answer, not model output |
| `tool` | `{name, status}` | status `running`, `done` or `failed`; names: `GetHealthProfile`, `GetDailyNutritionSummary`, `GetNutritionSummary`, `PrepareMeal` |
| `proposal` | `{card}` | a meal prepared by the assistant (`card.id`, `card.proposal` with matched items, grams, nutrients, totals, warnings); **nothing is saved** until `POST /api/nutrition/meals` with that `proposalId` |
| `delta` | `{text}` | zero or more |
| `done` | `{messageId}` | terminal, success |
| `error` | `{code, message, messageId}` | terminal; `ai_unavailable`, `ai_timeout`, `ai_empty_response` |

Problems detected before streaming are normal HTTP errors: `400` validation, `401`, `403 consent_required`,
`404` (conversation not yours or missing), `409 duplicate_message`, `429` rate limit, `503 ai_not_configured`. Nutrition adds `400 unknown_food`,
`invalid_range`, `range_too_long`, `409 proposal_not_open` (dismissed) and `404` for a proposal that is not the caller's.
A repeated `clientMessageId` in the same conversation is rejected with `409` so client retries never duplicate a message.
