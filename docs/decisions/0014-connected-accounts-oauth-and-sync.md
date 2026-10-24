# ADR-0014: Connected accounts: OAuth with single-use state, protected tokens, in-process idempotent polling

**Status:** Accepted

**Context.** The first integration (WHOOP) needs the user's authorization, long-lived credentials (refresh tokens that rotate and are single-use) and regular data pulls.
The credentials are as sensitive as the data; the callback is a browser redirect without our bearer token; the provider can be down, throttle or revoke access at any time.

**Decision.**
- **Authorization-code flow, read-only scopes**, started by an authenticated API call that creates a random 256-bit `state` (only its SHA-256 is stored, 10 minutes, single use).
  The anonymous callback identifies the user only through that state, consumes it before doing anything else, re-checks consent, links one provider account to one user (`account_already_linked`, `different_account`) and always ends in a redirect to the SPA with a stable code. The redirect target comes from configuration, never from the request.
- **Tokens are encrypted at rest** with ASP.NET Core Data Protection (a dedicated purpose string) and never returned, exported or logged. Losing the key ring does not lose data: the account is flagged for reconnection.
- **Refresh-token rotation is handled as a correctness problem**: one lock per account, the new pair is persisted before anything else (and not cancelled by a request abort), a rejected refresh means "needs reauthorization", and an access token rejected early gets exactly one refresh-and-retry.
- **Synchronization is idempotent and resumable**: batches are ingested as they arrive; windows overlap; failures end the run with a stable code (`reauthorization_required`, `rate_limited`, `provider_unavailable`, `consent_required`, `internal_error`) and the next cycle tries again.
  Provider response bodies and messages are never stored or logged; a malformed record is skipped and counted, never fatal for the page.
- **In-process scheduling**: a hosted service polls hourly for due accounts (modular monolith, no queue). Manual "Synchronize now" is throttled. Consent is checked before every run; withdrawing it stops imports without any provider call.
- **Provider abstraction** (`IWearableProvider`): authorization URL, code exchange, refresh, account id, streamed normalized fetch, revoke. WHOOP is the first implementation; the application, API and UI contain no WHOOP-specific branches except labels.
- Disconnecting revokes at the provider (best effort), removes the tokens, and keeps the imported history unless the user also asks to delete it.

**Alternatives.** Storing tokens in clear text with a "database is encrypted" argument (a leaked backup would be a leaked account); PKCE-only without a client secret (WHOOP issues a secret and says to keep it server-side); webhooks as the primary mechanism (needs a public URL and still needs polling for backfill);
a queue/worker service (more moving parts than one hourly job justifies now); letting the SPA hold provider tokens (never).

**Consequences.** Production needs a persistent, protected Data Protection key location (and backups of it) — documented and listed under "not yet done". The worker must not run on several instances without a distributed lock.
Everything provider-specific is unverified until a real WHOOP developer app is used; the unverified assumptions are listed in [whoop.md](../integrations/whoop.md) with their one-line fixes.
