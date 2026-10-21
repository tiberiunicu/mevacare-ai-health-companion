# ADR-0006: Deterministic emergency screen before the model

**Status:** Accepted

**Context.** Messages describing emergencies or self-harm must be met with directions to emergency help every time,
independent of model behaviour, provider outage or prompt tricks.

**Decision.** `EmergencyDetector` (English and Romanian regular expressions on a diacritic-stripped, lower-cased
message) runs before any model call. A match produces a fixed, pre-written, localized answer, persisted with
`IsSafetyResponse = true`; the model is not called. Self-harm takes precedence over a medical emergency. The system
prompt carries the same rules as a second layer for cases the screen misses.

**Trade-offs.** Keyword screens have false positives (kept deliberately: "I can't breathe through my nose" triggers)
and false negatives (any paraphrase or language not covered). It is tuned for recall and measured on a labelled
dataset (`tests/AIEvaluationTests/Datasets/emergency-triage.json`); the dataset caught a real gap during development
("ending my life"). A model-based classifier could be added as an additional layer, never as a replacement.

**Consequences.** Safety behaviour is unit-testable and deterministic. The dataset must grow with real failure cases,
and every new language needs its own rules and data.
