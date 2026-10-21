# ADR-0003: Own authentication with PBKDF2, short-lived JWT and rotating refresh cookie

**Status:** Accepted

**Context.** Credentials are stored locally, so hashing and session handling must be sound. Options: ASP.NET Core
Identity (a full framework with its own schema and UI assumptions), an external identity provider (Entra ID, Auth0,
Keycloak), or a small purpose-built implementation on framework primitives.

**Decision.** Purpose-built, using framework primitives only: `PasswordHasher<T>` (PBKDF2-HMAC-SHA512, 210,000
iterations, versioned format enabling re-hash), `JsonWebTokenHandler` for 15-minute HS256 access tokens, and opaque
refresh tokens (32 random bytes, SHA-256 stored) delivered as an httpOnly, SameSite=Strict, path-scoped cookie and
rotated on every use. Tokens share a *family*; reuse of an already-rotated token after a short grace window (for
concurrent tabs) revokes the family. Lockout and per-IP rate limits protect login.

**Why not Identity or an IdP now.** The requirement list names exactly `Users`, `UserRoles`, `UserConsents`; Identity
would add many unused tables and a different consent story. An IdP is the right production answer for MFA and e-mail
flows, and the seam is narrow: only `AuthService`, `TokenService` and the `ICurrentUser` claim would change.

**Consequences.** Less code to trust and teach, but we own the gaps: no e-mail verification, password reset or MFA yet
(see [security-and-privacy.md](../architecture/security-and-privacy.md#not-yet-done)). HS256 means the API both signs
and validates; moving to asymmetric keys is a change in `TokenService` and `ConfigureJwtBearer`.
