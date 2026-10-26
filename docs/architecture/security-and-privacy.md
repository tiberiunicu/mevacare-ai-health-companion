# Security and privacy

Health data is highly sensitive; the design assumes that from the first commit. This page lists what is implemented,
what is deliberately configurable, and what is **not done yet**. Use only synthetic data until the *Not yet done* list
is closed.

## Implemented controls

| Concern | Control |
|---|---|
| Passwords | PBKDF2-HMAC-SHA512 through ASP.NET's `PasswordHasher` (210,000 iterations, configurable, transparent re-hash on login); length-based policy (10–128 characters, no composition rules); never logged or returned |
| Sessions | 15-minute JWT (HS256, pinned algorithm, 30 s skew) with only `sub`, `jti` and roles; rotating refresh token in an httpOnly, SameSite=Strict, path-scoped cookie; only a SHA-256 hash is stored; reuse of a rotated token after a grace window revokes the whole token family |
| Brute force | per-account lockout (5 failures → 15 min), per-IP rate limit on register/login/refresh, identical errors and equalised timing for unknown user vs wrong password |
| Authorization | secure by default: the fallback policy requires authentication; the caller id comes only from the validated token (`ICurrentUser`); every query filters by it; foreign ids answer 404 |
| Mass assignment | endpoints bind to dedicated request records, never to entities; tests post extra fields (`roles`, `userId`, `id`) and assert they are ignored |
| Input validation | FluentValidation on every body, ranges also enforced by SQL check constraints, request length limits, enum validation |
| Transport/headers | HSTS and HTTPS redirection outside Development, `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` on all `/api` responses |
| Secrets | none in the repository; connection string, JWT key and AI keys come from user-secrets or environment variables; startup fails fast on a weak/missing JWT key; key values never appear in errors or logs |
| Logging | no health data, tokens, passwords, chat content or provider error bodies; request path without query string |
| Audit | sign-in/out/failures/lockout, token-reuse detection, consent grants/revocations (with policy version), profile/goal changes, export, deletion, chat sends and safety overrides; no payloads |
| Data minimisation toward the LLM | the system prompt, the conversation's recent messages (bounded by count and characters), the new message and, only when the model calls a tool, that tool's compact result (for example daily totals, or age/sex/height/activity level; never the date of birth, e-mail, name, tokens or time zone). Checked against recorded provider requests in manual testing. Consent for health data **and** AI processing is required for health tools. |
| Frontend | in-memory access token, no `localStorage` of credentials, no raw HTML rendering of model output, no third-party requests |

## GDPR design

| Right / duty | Implementation |
|---|---|
| Lawful basis, consent (art. 6, 7, 9) | `UserConsents`: `TermsOfService` (required), `HealthDataProcessing`, `AiProcessing`, `ExternalIntegrations`. Health-data endpoints answer **403 `consent_required`** until granted and again after withdrawal; chat requires `AiProcessing`. Optional consents default to *off* and are separate checkboxes at sign-up. Each change is audited with the policy version. |
| Access and portability (art. 15, 20) | `GET /api/account/export` returns one JSON document: account, consents and a section from every module's `IUserDataExporter` (health profile, goals, preferences, chat history). New modules must register an exporter. |
| Erasure (art. 17) | `POST /api/account/delete` (re-authentication by password) deletes the user; every module's rows go away through cascading foreign keys (integration tests assert profile and chat data are gone). Only the pseudonymous audit trail remains. |
| Withdrawal | withdrawing a consent blocks further processing immediately; existing data stays until the user deletes it (a delete-data-per-consent action is future work). |
| Records of processing | `core.AuditEvents` and this document. |

### Proposed retention (to be confirmed by the controller/DPO before production)

| Data | Proposal |
|---|---|
| Account, profile, goals, preferences, chat history | until the user deletes them or the account |
| Refresh tokens | until expiry (14 days) or revocation; purge expired rows periodically (job not yet written) |
| Audit events | 12 months, then delete (job not yet written) |
| Logs | 30 days, no health data inside |

## AI-specific security

- The assistant has **eight tools** (profile, two nutrition summaries, meal preparation, medical-knowledge search, weight history, sleep and recovery, computed insights). All are bound to the caller, re-check consent and are read-only except meal preparation, which only proposes. They are bound per turn to the signed-in user
  (no user-id parameter exists), re-check consent, are audited, and the only write-type tool merely prepares a proposal that the user must
  confirm in the UI; confirmations are idempotent. A prompt injection can therefore at worst make the assistant read the **caller's own**
  data or prepare a meal the user then rejects. See [tool-calling.md](../ai/tool-calling.md).
- Medical knowledge (Phase 4): documents enter only through an operator CLI (no HTTP ingestion), with a licence allow-list and recorded provenance; retrieved text is treated as untrusted data (labelled, quarantined when it looks like instructions, never in the system role). `GET /api/knowledge/sources` lists source metadata to signed-in users; `/health/knowledge` is anonymous and exposes no secrets. **Privacy:** when the user asks a knowledge question, the model's search *query* is sent to the embedding provider (OpenAI/Ollama; nothing leaves the machine with `Lexical` or a local Ollama). The vector store holds no user data, but Qdrant has no authentication unless `Knowledge:VectorStore:ApiKey` is set: do not expose it beyond localhost or a private network, and use TLS and a key in any shared deployment. Citation excerpts are stored with the assistant message, so they are deleted with the account like the rest of the conversation.
- User messages are untrusted: they are only ever sent in the *user* role, never concatenated into the system prompt.
- Emergency and self-harm messages are answered by fixed text and never reach the model.
- Model output is rendered as Markdown without raw HTML.
- No shell or SQL execution from model output exists or is planned.
- See [AI safety](../ai/ai-safety.md) for limits of these defences.

## Insights and AI explanations (Phase 6)

- Insights are computed from the caller's own data through the contracts that check the health-data consent and filter by user; nothing is stored, so there is no new personal-data table, export section or deletion path.
- The AI explanation sends **aggregated, application-computed facts** (trends, averages, rule outputs, provider keys) of the caller to the configured AI provider, only after the AI-processing consent. It sends no raw records, identifiers, names or free text, has no tools, is rate limited per user and is not stored. Provider errors are reduced to stable codes.
- Output is checked for numbers that are not in the data it was given; wellness disclaimers are fixed text. Neither replaces a clinical review of the wording ([../remaining-work.md](../remaining-work.md)).

## Wearable integrations (Phase 5)

- **Consent**: connecting and synchronizing need the *External integrations* and *Health data processing* consents; withdrawing either stops imports before any provider call.
- **Scopes** are read-only and limited to what is shown (profile id, sleep, recovery, cycles, workouts); the e-mail address and name that WHOOP returns for the profile are never stored.
- **OAuth**: random 256-bit `state`, stored only as a hash, single use, 10 minutes; the anonymous callback identifies the user only through it, the redirect target is configuration, one provider account can be linked to one user.
- **Tokens** are encrypted with Data Protection, never returned by the API, exported or logged; the client secret lives only in user-secrets/environment. **Key management is an operator duty**: set `Integrations:DataProtectionKeysPath` to a persistent, access-controlled, backed-up location; losing it forces reconnection (data is unaffected).
- **Provider errors** are mapped to stable codes; response bodies are never stored or logged.
- **Disconnect and deletion**: disconnecting revokes access at the provider (best effort) and removes the tokens; the user chooses whether the imported history stays. Account deletion removes connections and measurements; the export lists them (never tokens).
- **What is sent where**: only the authorization request and token calls go to WHOOP; MevaCare sends no health data to WHOOP. Imported data is sent to the AI provider only through the read tools when the user asks, under the AI consent.

## Not yet done

These must be resolved before real personal health data is used:

1. **E-mail verification and password reset** — accounts are not verified; registration reveals whether an e-mail
   exists (409), a deliberate trade-off until e-mail verification exists.
2. **Multi-factor authentication.**
3. **Transport security in deployment** — HTTPS termination, certificate management and a reverse-proxy configuration
   (forwarded headers, SSE buffering off) are not provided; a Content-Security-Policy for the SPA host is not set.
4. **Encryption at rest** for the database (TDE or disk encryption) and a key-management story. Provider tokens are already encrypted at the application level; the Data Protection key ring location and its backup are still an operator task.
5. **Retention jobs** (expired refresh tokens, old audit events) and backups with matching retention.
6. **Legal texts**: the Terms of Service and Privacy Policy referenced by the consent screen are not written; the
   policy version string is a placeholder for them. A DPIA and processor agreements with the chosen LLM provider are
   required before sending real data to it.
7. **Admin tooling and role-based authorization** — roles exist in the token and database but no admin endpoint does.
8. **Penetration test and dependency monitoring** in CI.
9. **Provider data handling**: choosing a provider/region/zero-retention setting is an operator decision; see the
   *What is sent* section in the AI settings page and [LLM integration](../ai/llm-integration.md).
