# WHOOP integration

> **Verification status — read this first.** The integration was written from WHOOP's public developer documentation
> (https://developer.whoop.com, API v2) and the automated tests use a **scripted stand-in** that answers with the documented response
> shapes, rotating refresh tokens, paging and failures.
> **The owner has since connected a real WHOOP member account from a local installation** (registered developer app, OAuth connection,
> and a synchronization started from the application and from the chat). That confirmed the connection assumptions listed as
> *confirmed* under [Assumptions](#assumptions). **Not done:** a comparison of the imported values with the WHOOP app one by one,
> a long-running check of the refresh token, and a connection from the live demo (its redirect URL is not registered at WHOOP).

## What it does

A signed-in user connects their WHOOP account (OAuth 2.0 authorization-code flow, read-only scopes). The application then imports,
keeps up to date and shows:

| WHOOP data | Stored as | Shown in |
|---|---|---|
| Sleep (`/v2/activity/sleep`): duration, stage times, performance/efficiency/consistency %, respiratory rate, disturbances, naps | `SleepSession` | Sleep and recovery page, dashboard |
| Recovery (`/v2/recovery`): recovery score, resting heart rate, HRV (RMSSD) | `HealthMeasurement` (`RecoveryScore`, `RestingHeartRate`, `HrvRmssd`) | Sleep and recovery page, dashboard |
| Physiological cycle (`/v2/cycle`): day strain, energy (kJ → kcal) | `HealthMeasurement` (`DayStrain`, `CaloriesBurned`) | Activity page, dashboard |
| Workouts (`/v2/activity/workout`): sport, strain, heart rate, energy, distance | `Workout` | Activity page |

Not imported (deliberately, for now): body measurements (WHOOP's weight/height, a current value without history — weight is entered
by the user or comes from a scale), SpO₂ and skin temperature, step counts, heart-rate series, zone durations, webhooks.

## Setting it up (developer machine)

1. **Register an app** in the WHOOP developer dashboard (https://developer-dashboard.whoop.com): create a team, then an app.
   Select the scopes `read:profile`, `read:recovery`, `read:cycles`, `read:sleep`, `read:workout` (the application also requests
   `offline`, which WHOOP requires to issue a refresh token). New apps start in a *Sandbox* tier limited to 10 WHOOP members, which
   is enough for development. Testing needs a WHOOP member account.
2. **Register the redirect URL** exactly as the application will send it, by default
   `http://localhost:5080/api/integrations/whoop/callback` (the API; the browser is redirected here by WHOOP and then forwarded to the
   web app). If WHOOP does not accept an `http://localhost` redirect (its documentation does not say), expose the API through an
   HTTPS tunnel and use that URL here and in `Integrations:Whoop:RedirectUri`; set `Integrations:FrontendBaseUrl` to where the SPA is served.
3. **Give the application the credentials** (never in the repository):

   ```powershell
   $api = 'src/backend/MevaCare.AI.HealthCompanion.Api'
   dotnet user-secrets set "Integrations:Whoop:ClientId"     "<client id>"     --project $api
   dotnet user-secrets set "Integrations:Whoop:ClientSecret" "<client secret>" --project $api
   # only if you changed the redirect URL registered in the dashboard:
   dotnet user-secrets set "Integrations:Whoop:RedirectUri"  "<registered url>" --project $api
   ```

4. Start the API and the web app, turn on **Settings → Privacy & Consent → External integrations** (and health data processing),
   open **Connected devices**, press **Connect WHOOP**, approve at WHOOP. You are returned to the app, which imports the last 30 days
   right away. Afterwards the data is refreshed in the background about every hour and on **Synchronize now**.

Until the credentials exist, *Connected devices* shows "Not set up on this server" and `POST /api/integrations/whoop/connect`
answers `503 integration_not_configured`. Nothing else in the application depends on WHOOP.

### Configuration (`Integrations:*`)

| Key | Default | Meaning |
|---|---|---|
| `Whoop:ClientId`, `Whoop:ClientSecret` | — | from the developer dashboard (secret: user-secrets / environment only) |
| `Whoop:RedirectUri` | `http://localhost:5080/api/integrations/whoop/callback` | must equal a redirect URL registered at WHOOP |
| `Whoop:Scopes` | `offline read:profile read:recovery read:cycles read:sleep read:workout` | read-only |
| `Whoop:ClientAuthentication` | `Post` | `Post` = client id/secret in the form body, `Basic` = HTTP Basic (see assumptions) |
| `Whoop:AuthorizationEndpoint`, `TokenEndpoint`, `ApiBaseUrl` | WHOOP production URLs | can point at a local stand-in for testing |
| `FrontendBaseUrl` | `http://localhost:5173` | where the browser is sent after the callback (never taken from a request) |
| `DataProtectionKeysPath` | framework default | folder of the key ring that encrypts stored tokens; **set it in production** |
| `Sync:BackgroundEnabled`, `PollMinutes`, `IntervalMinutes` | `true`, `15`, `60` | periodic synchronization |
| `Sync:InitialBackfillDays`, `OverlapDays`, `MaxPagesPerCollection`, `MinManualIntervalSeconds` | `30`, `7`, `100`, `60` | first import window, re-read window, safety cap, throttle for **Synchronize now** |

## How the data is normalized

Rules (code: `Providers/Whoop/WhoopMapper.cs`, covered by unit tests):

- **Time**: every record keeps WHOOP's own `timezone_offset`; storage is UTC plus the offset and the **local date** in that offset.
  A sleep belongs to the local date on which it **ended** ("the night of Tuesday" is the sleep that ends on Tuesday); a workout to the
  local date it started; a cycle to the local date it started (WHOOP's own app may assign cycles to days differently — unverified).
  A recovery has no time of its own and is dated at the end of the sleep it belongs to.
- **Units**: milliseconds → whole minutes (rounded), kilojoules → kilocalories (÷ 4.184), HRV stays RMSSD in ms.
- **Scoring**: only records with `score_state = SCORED` carry values. A pending or unscorable sleep/workout is stored without values
  (and updated when a later synchronization sees it scored); pending recoveries and cycles are skipped until scored.
- **Plausibility**: values outside physical ranges (recovery > 100, strain > 21, negative durations …) are dropped, one value at a time.
- **Scores are WHOOP's own.** Recovery score and strain are separate metrics, always labelled with their provider in the API and UI,
  averaged per provider, and never merged with another device's numbers. HRV is stored as RMSSD because other devices report SDNN, which is not comparable.
- **Duplicates**: each record is stored once, keyed by (source, metric, WHOOP record id); re-reading the same window updates rows in place.

## Operating it

- **Tokens** are encrypted with ASP.NET Core Data Protection before they are stored and never appear in API responses, logs or the GDPR
  export. Refresh tokens are single-use at WHOOP: each refresh replaces the stored pair immediately, under a per-account lock.
  If the key ring is lost the stored tokens cannot be read; the account is flagged *needs to be reconnected* (the history stays).
- **Failure codes** on a synchronization run: `reauthorization_required` (WHOOP refused the tokens — the user reconnects),
  `rate_limited` (WHOOP allows 100 requests/minute and 10,000/day by default; retried on the next cycle), `provider_unavailable`,
  `consent_required` (a consent was switched off — nothing is called), `internal_error`. Provider messages are never stored.
- **Background worker** runs inside the API process. Run a single API instance, or move the worker out before scaling horizontally.
- **Webhooks** (WHOOP can push `recovery`/`sleep`/`workout` updates) are not implemented; polling every hour is the design. Webhooks would need a public HTTPS URL.
- **Disconnecting** asks WHOOP to revoke the access (best effort), deletes the stored tokens and optionally the imported data. Deleting the MevaCare account removes everything.

## Assumptions

These come from gaps or inconsistencies in WHOOP's documentation. Each has a cheap fix. The *Status* column says what the real connection showed.

| Assumption | Status | If wrong |
|---|---|---|
| Token requests are accepted as `application/x-www-form-urlencoded` (WHOOP's refresh example shows a JSON body) | **Confirmed** by the real connection (the first token exchange succeeded) | change the content type in `WhoopProvider.RequestTokensAsync` |
| Client credentials in the form body (`Post`) are accepted | **Confirmed**: the real connection works with the default `Post` | set `Whoop:ClientAuthentication=Basic` |
| `http://localhost…` redirect URLs can be registered | **Confirmed**, when the registered URL is identical, character by character, to `RedirectUri` (a mismatch gives `invalid_request … redirect_uri`) | use an HTTPS tunnel and set `RedirectUri`/`FrontendBaseUrl` |
| `offline` in the scope list yields a refresh token on the first exchange | **Confirmed**: the connection was not refused with `no_refresh_token` | the connection is refused with `no_refresh_token` |
| Rate-limit reset is signalled with `X-RateLimit-Reset` (seconds) | Not checked | only affects the log/retry hint |
| A cycle's local date is the date of its `start` | Not checked against the WHOOP app | cycles may sit one day off in the Activity page |
| Refresh-token lifetime is long enough for hourly use | Not checked over a long time | accounts flip to *needs to be reconnected* |

## Testing without a WHOOP account

`tests/IntegrationTests/Support/WhoopSupport.cs` contains the scripted WHOOP used by the automated tests (run with the whole suite).
For a manual browser run, point `Whoop:AuthorizationEndpoint`, `TokenEndpoint` and `ApiBaseUrl` at any local server that answers with
the shapes in `FakeWhoopHandler`; Phase 5 was checked this way in the browser (connect → import → pages → disconnect), with
synthetic data only. That proves the application's flow, not WHOOP's behaviour.
