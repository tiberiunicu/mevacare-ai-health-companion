# ADR-0001: Modular monolith with Clean Architecture boundaries

**Status:** Accepted

**Context.** The product must be maintainable by a small team, eventually become a commercial SaaS, and also serve as
a learning project. Microservices, Kubernetes, messaging and multi-agent systems are explicitly out of scope.

**Decision.** One ASP.NET Core application, split into modules (`Identity`, `HealthProfile`, `Assistant`, later
`Nutrition`, `Measurements`, `Knowledge`, `Integrations`). Each module owns its domain, persistence configuration,
services and endpoints, organised by feature. Dependencies point inward: modules depend on `Infrastructure` and
`Contracts`; the `Api` project composes. Modules talk through small interfaces in `Contracts`, never through each
other's services. No MediatR: one service class per feature is enough.

**Consequences.** Simple deployment and debugging, in-process calls, one transaction scope when needed. Boundaries are
enforced by project references and review, not by the network, so discipline matters. A module can be extracted into a
service later because it already depends only on contracts. The one allowed cross-module type reference is
`Identity.User` for foreign keys (see [ADR-0002](0002-single-dbcontext-schema-per-module.md)).

**Deviation from the suggested structure.** Only modules that exist are created (see
[ADR-0007](0007-incremental-modules-and-migrations.md)).
