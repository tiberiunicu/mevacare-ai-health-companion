# ADR-0004: Consent gates data access; export and deletion through module contracts

**Status:** Accepted

**Decision.** Four consent types are stored in `UserConsents` (current state) with the full history in the audit log.
`IConsentService.EnsureGrantedAsync` is called by services that process health data (profile, goals) and by chat
(`AiProcessing`); failure is HTTP 403 `consent_required`, which the UI turns into a link to *Privacy & Consent*.
Display preferences contain no health data and need no consent. Every module that stores personal data implements
`IUserDataExporter` for the export, and erasure relies on cascading foreign keys (verified by integration tests).

**Why.** Consent has to be enforced where data is used, not only collected; a single interface keeps the rule uniform
for future modules. Using the database for erasure removes the risk of a module forgetting to delete.

**Consequences.** Withdrawal blocks access but does not delete data automatically. Legal texts and policy versions are
placeholders until written (`ConsentPolicy.CurrentVersion`). Consent for third-party AI processing is separate from
health-data consent because it involves a different recipient.
