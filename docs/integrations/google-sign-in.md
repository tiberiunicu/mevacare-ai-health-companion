# Sign in with Google

Status: **built and tested with a stand-in for Google; not yet run against the real Google** (there was no OAuth client when it was written). The part that cannot be proven without Google is listed under *Unverified* below.

## What the user gets

- **Login and registration pages:** a Google-drawn button under the form. A person who already has a MevaCare account linked to Google signs in with one click. A person with no account is asked to accept the Terms of Service (and may give the two optional consents, as on the registration form) and the account is created.
- **Settings → Privacy & Consent → Sign-in methods:** a signed-in person can **connect** their Google account to an existing (password) account, and **disconnect** it again while the account still has a password.
- **Accounts created with Google have no password.** They sign in with Google only. Deleting such an account is confirmed by signing in with Google once more (instead of typing a password).
- The button appears only when the server has the feature switched on. With no client id configured nothing changes anywhere.

## How it works

```
Browser (Google Identity Services)            API                                  Google
  1. user clicks the Google button  ───────────────────────────────────────────►  sign-in popup
  2. ◄──────────────────────────────  signed ID token (JWT, RS256, ~1 h)  ───────
  3. POST /api/auth/google { idToken } ──►  verify signature (Google's public keys),
                                            issuer, audience = our client id, expiry,
                                            email_verified = true
                                            find account by Google "sub"  → sign in
                                            else e-mail matches a password account → 409 google_account_not_linked
                                            else needs acceptTerms → 409 registration_required / create the account
  4. ◄── our own access token (JSON) + httpOnly refresh cookie  (same as a password sign-in)
```

The browser only ever delivers Google's token; **the API decides who the user is**, and from then on the session is the application's own (ADR-0003): 15-minute access token in memory, rotating httpOnly refresh cookie. There is **no client secret**: the ID-token flow needs only the public client id, so nothing secret is stored or can leak.

## Setup (once, about ten minutes)

1. Open [Google Cloud Console](https://console.cloud.google.com/) → create or choose a project → **APIs & Services → OAuth consent screen**: user type *External*, app name, support e-mail; add the scopes `openid`, `email`, `profile` (they are the defaults for sign-in and need no Google review). While the app is in *Testing*, only the test users you list can sign in; publish it to allow anyone.
2. **APIs & Services → Credentials → Create credentials → OAuth client ID → Web application.**
   - **Authorized JavaScript origins:** `http://localhost:5173` for development, and your real `https://…` domain in production.
   - **Authorized redirect URIs:** none (the ID-token flow has no redirect).
3. Copy the **Client ID** (`1234567890-abc.apps.googleusercontent.com`) — *not* the client secret, which is not used.
4. In the Terminal panel:
   ```powershell
   ./scripts/setup-google-signin.ps1 -ClientId '1234567890-abc.apps.googleusercontent.com'
   ```
   It stores `Auth:Google:ClientId` in the API's user-secrets. In production set the environment variable `Auth__Google__ClientId`. Restart the API.
5. Open the login page: the Google button is there. `./scripts/setup-google-signin.ps1 -Remove` switches it off.

Configuration: `Auth:Google:ClientId` (empty = off) and `Auth:Google:DiscoveryUrl` (Google's OpenID Connect document; only tests change it).

## Rules that protect accounts

| Situation | Behaviour | Why |
|---|---|---|
| Google identity already linked | signs in to that account, even if the e-mail address changed at Google | the stable `sub` identifies the person, not the e-mail |
| E-mail matches an account **created with a password** and not linked | **refused** (`google_account_not_linked`); the owner signs in with the password and connects Google in Settings | registration does not verify e-mail addresses: someone could have registered a stranger's address first; silently merging would hand the real owner an account the other person can still sign in to |
| Google reports the e-mail as not verified | refused (`google_email_not_verified`) | an unverified address proves nothing |
| New identity | account created only after `acceptTerms` (`registration_required` otherwise) | the Terms must be accepted before an account exists, as on the registration form |
| One Google identity, two accounts | refused (`google_in_use`); one account, two Google identities also refused | a unique index on (provider, sub) and on (user, provider) |
| Disconnect on an account without a password | refused (`password_required`) | the person would be locked out |
| Password sign-in for an account without a password | the usual "invalid e-mail or password" after the usual time | no hint about how the account was created |
| Delete account | password, or for a password-less account a fresh Google token of **the same identity** | re-authentication, as before |
| Password-lockout state | a Google sign-in does not clear it | lockout protects the password, and Google proves nothing about it |

Token checks (unit-tested with a locally generated RSA key): signature against Google's published keys (cached and refreshed by `ConfigurationManager`), `iss` is `https://accounts.google.com` or `accounts.google.com`, `aud` is exactly our client id, not expired (30 s skew), algorithm RS256 only (`none` and others are rejected), `sub` and `email` present, `email_verified` must be true. If Google cannot be reached the answer is 503 `google_unavailable`, never "unauthorized".

## Unverified (needs a real client id and a real Google account)

- That Google's actual tokens carry the claims the way the validator reads them (`sub`, `email`, `email_verified` as boolean or text, `name`) — taken from Google's documentation, tested with synthetic tokens of that shape.
- That `http://localhost:5173` as an authorized origin works in the owner's Google project and that the sign-in popup is not blocked by the browser.
- **No `nonce` is used.** An ID token that is stolen from the browser within its hour of validity could be replayed to `POST /api/auth/google`. The token is delivered to the page by Google's script over HTTPS and is never stored by the application; adding a server-issued, single-use nonce is listed in [remaining-work.md](../remaining-work.md).
- **Content-Security-Policy:** none is set today. When one is added, Google's script needs `https://accounts.google.com/gsi/` in `script-src`, `frame-src`, `connect-src` and `style-src`; the `Cross-Origin-Opener-Policy` header must not be `same-origin` on the SPA or the popup cannot hand the token back.
- Not built: setting a password on an account created with Google, switching the sign-in e-mail address, other providers (Microsoft, Apple).
