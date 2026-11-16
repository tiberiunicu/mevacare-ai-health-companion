# ADR-0020: Password reset by e-mail: one-time hashed link, queued mail, every session ended

**Status:** Accepted and built; tested with a capturing stand-in for the mail queue (no real mail server was used). Not yet deployed.

**Context.** People forget passwords and the application had no way back in. The application also had no e-mail sending at all, and its registration does not verify e-mail addresses.

**Decision.**
- **A link, not a code**, valid 30 minutes (`Auth:PasswordResetMinutes`) and usable once. The token is 32 random bytes; only its SHA-256 hash is stored (`identity.PasswordResetTokens`), like refresh tokens. A row version makes "use it" atomic: of two simultaneous uses one succeeds. A newer link cancels older ones; at most one e-mail per account every two minutes.
- **No account enumeration.** `POST /api/auth/forgot-password` answers `202` whatever the address. The e-mail is put in a queue and sent by a background service, so the request takes the same time whether or not the account exists. (A small difference remains: one extra database round trip for existing accounts. It is far below network noise and rate-limited, and is listed as accepted.)
- **The token travels in the URL fragment** (`/reset-password#token=…`): browsers do not send fragments to servers, proxies, logs or the `Referer` header. The page reads it once and removes it from the address bar.
- **The link is built from `Auth:PublicBaseUrl`**, never from the request's `Host`, so a forged header cannot point a reset e-mail at another site.
- **Using the link** sets the password (same policy as registration), **ends every session** (all refresh tokens revoked), lifts a lockout (the lockout protected a password that no longer exists), cancels other open links and e-mails "your password was changed". It does **not** sign anyone in: the person signs in with the new password.
- **Accounts created with Google** (no password) can use the same flow; it gives them a password. The e-mail link proves control of the address, which is what Google vouched for.
- **E-mail infrastructure** is general, not password-specific: `IEmailQueue` (Contracts), an in-memory channel and a background dispatcher with three attempts (Infrastructure), SMTP through MailKit. With no `Email:Host` the queue reports `IsConfigured = false`, `GET /api/auth/providers` says `passwordReset: false` and the login page hides the link; nothing is sent and logs never contain addresses or message text.
- Plain-text messages in English and Romanian (the language the app was showing).

**Alternatives.** A numeric code typed in the page (more steps, brute-forceable, no benefit here). Token in the query string (leaks through logs and `Referer`). Sending the e-mail inside the request (reveals which addresses exist by timing and makes the request depend on the mail server). Signing the person in after the reset (a stolen link would then be a login). Third-party e-mail API instead of SMTP (another processor and key; SMTP works with the mailbox the owner already has).

**Consequences.**
- One table, one migration (`AddPasswordResetTokens`), two endpoints, a flag in `/providers`, one dependency (MailKit).
- The queue is in memory: a restart can lose a message that was not yet delivered; the person asks again.
- Not built: e-mail verification at registration, "change password" while signed in, e-mail when someone signs in from a new place, cleanup of old token rows (part of the retention job in [remaining-work.md](../remaining-work.md)).
- Gmail as the mail server needs an app password and a sender equal to the mailbox; it is fine for a demo, not for volume.
