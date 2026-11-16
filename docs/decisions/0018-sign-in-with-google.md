# ADR-0018: Sign in with Google: ID token verified by the API, no automatic account merging

**Status:** Accepted and built (verified with a stand-in for Google only; see [integrations/google-sign-in.md](../integrations/google-sign-in.md)).

**Context.** The owner wants people to sign in and sign up with Google. The application has its own accounts, sessions and consents
(ADR-0003, ADR-0004), registration does not verify e-mail addresses, and the project keeps secrets out of the repository.

**Decision.**
- **Google Identity Services + ID token.** The browser shows Google's button and receives a signed ID token; it posts it to
  `POST /api/auth/google`. The API verifies it itself (signature with Google's published keys, issuer, audience = our client id,
  expiry, RS256 only, verified e-mail) and then issues the **application's own session**, exactly like a password sign-in. Nothing
  about authorization changes: JWT bearer, rotating refresh cookie, consents.
- **No client secret.** The ID-token flow needs only the public client id, so there is no secret to store, rotate or leak. The id is
  served to the page by `GET /api/auth/providers`, which also decides whether the button exists at all (empty id = feature off).
- **The Google `sub` is the identity**, stored in `identity.ExternalLogins` (unique on provider+sub and on user+provider), never the e-mail.
- **No automatic merging by e-mail.** A Google identity whose e-mail matches a password account is refused with
  `google_account_not_linked`; the owner signs in with the password and connects Google in Settings. Reason: registration does not
  verify e-mail addresses, so an account for someone else's address may have been created by an attacker who knows its password.
- **Accounts created with Google have no password** (`Users.PasswordHash` is now nullable; `hasPassword` is in the user DTO). They cannot
  disconnect Google (it would lock them out) and confirm account deletion with a fresh Google token instead of a password.
- **Terms first.** A new identity gets an account only with `acceptTerms`; the login page shows the same consent checkboxes as the
  registration form for that one step and keeps the token in memory.
- A seam (`IGoogleIdTokenValidator`, `IGoogleSigningKeys`) lets integration tests sign in as any Google identity and unit tests verify
  the token rules with a generated RSA key; the real Google is the one thing they cannot test.

**Alternatives.** The OAuth *authorization-code* flow with a redirect (like WHOOP, ADR-0014): needs a client secret, a callback
endpoint, state and PKCE handling and a second page flow, with no benefit for sign-in only. ASP.NET's `AddGoogle()` cookie handler:
a second session system next to ours. Merging by verified e-mail: convenient but unsafe while our own e-mails are unverified; it can
be reconsidered once registration verifies e-mail addresses. A third-party identity service: another moving part and cost.

**Consequences.**
- One new table, one nullable column (migration `AddGoogleSignIn`), five endpoints (`providers`, `google`, `google/link`, `DELETE google`, and the delete-account body gained `googleIdToken`).
- The login page loads Google's script from `accounts.google.com` only when the feature is on; a future Content-Security-Policy must allow it.
- Not built: a single-use nonce against replay of a stolen ID token, setting a password on a Google-created account, other providers.
