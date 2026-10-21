# ADR-0002: One `AppDbContext`, one SQL schema per module

**Status:** Accepted

**Context.** Alternatives: (a) a DbContext per module with no cross-module foreign keys; (b) a single DbContext that
references every module (a dependency cycle with module services); (c) a single DbContext that discovers module
configurations.

**Decision.** Option (c). `Infrastructure` owns `AppDbContext` and receives the module assemblies from the host
(`PersistenceModelOptions`), applying their `IEntityTypeConfiguration` classes. Modules query via `Set<T>()`; the
context has no `DbSet` properties. Each module's tables sit in their own schema (`identity`, `health_profile`,
`assistant`, `core`). Migrations are in `Infrastructure`; the design-time host is `Api`.

**Why not one context per module.** The product needs real foreign keys to `Users` with cascading delete so that GDPR
erasure is a single database operation that cannot silently miss a module. Separate contexts would turn that into
application code that every new module must remember.

**Consequences.** One migration history and one transaction scope. Modules share the physical database and may
reference `Identity.User` for foreign keys only. Splitting a module out later means moving its schema and replacing the
foreign key with an id plus an erasure event.

**Conventions chosen here.** GUID v7 ids generated in code with `ValueGeneratedNever` (otherwise EF treats new entities
reached through navigations as existing), UTC `datetime2`, string-stored enums, `AuditableEntity` timestamps set by an
interceptor, no generic repository (EF Core is already a unit of work and repository; services use it directly).
