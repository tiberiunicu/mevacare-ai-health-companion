# Prompt engineering and AI safety

## What problem it solves

A health assistant can cause harm by sounding authoritative about diagnoses, medication or diets, by missing an
emergency, by inventing personal data, or by being talked out of its rules. The aim is a wellness companion that
explains, qualifies uncertainty and sends people to professionals.

## Layers

| Layer | Where | What it does | Deterministic? |
|---|---|---|---|
| 1. Emergency screen | `Safety/EmergencyDetector.cs` | first-person emergency or self-harm text → fixed localized answer, **model not called** | yes |
| 2. System prompt | `Safety/SystemPrompts.cs` | scope, no diagnosis/prescription/medication changes, no invented personal data, no extreme diets, emergency guidance, uncertainty, untrusted input, no prompt disclosure, reply language | no (model-dependent) |
| 3. Structural isolation | `AiChatService` | user text only in the *user* role; the system prompt is a constant; no tools, no retrieved text, no code or SQL execution | yes |
| 4. Output handling | frontend | Markdown without raw HTML | yes |
| 5. Evaluation | `tests/AIEvaluationTests` | measures layers 1–2 against labelled and adversarial datasets | n/a |

### Emergency screen

Regexes (English and Romanian) run on a lower-cased, diacritic-stripped copy of the message with a 100 ms match timeout
(a timeout counts as a match: fail safe). Categories: `MedicalEmergency` (chest pain with red-flag context, cannot
breathe, stroke signs, heavy bleeding, overdose/poisoning, throat swelling, seizure/unconscious) and `SelfHarm`
(which wins when both match). The answers name emergency numbers (112 EU, 911 US, 988 US crisis line) and
`findahelpline.com`; no country-specific hotline numbers are invented.

### System prompt

Contains a **canary** token. If it ever shows up in an answer the model leaked its instructions; the live evaluation
checks for it. The prompt is also explicit that the assistant currently has **no access** to the user's data, so it must
not invent numbers; this changes in Phase 3 together with tools and their own rules.

## Prompt-injection stance

User messages are untrusted and can only *argue* with the model, not edit its instructions. Phase 2 has no tools, no
retrieved documents and no secrets in the prompt, so the worst outcome of an injection is an off-policy *text* answer.
That is why the live adversarial evaluation exists, and why later features add controls instead of trusting the prompt:
tool calls are validated and authorised server-side, writes need explicit user confirmation, retrieved documents will be
labelled untrusted context ([tool-calling.md](tool-calling.md), [rag-embeddings-memory.md](rag-embeddings-memory.md)).

## Limitations (read these)

- The emergency screen is a **keyword heuristic**: it misses paraphrases and other languages and fires on some benign
  phrases (accepted for recall; documented in `knownLimitations` in the dataset).
- The system prompt is guidance, not enforcement: a model can still produce an unsafe answer. Nothing here is a
  clinical safety guarantee, and the product must keep presenting itself as wellness information only.
- No output moderation or PII filter runs on model answers yet.
- No jailbreak or red-team coverage beyond the small dataset; medical-claim accuracy is not evaluated.
- Romanian system-prompt behaviour depends on the chosen model.

## How it is tested

- `EmergencyTriageEvaluationTests` — 30 labelled emergencies (100 % recall required), 26 benign questions (0 false
  positives required), category and language checks, diacritics, case, regex time budget, known limitations.
- `ChatTests` — emergency messages never reach the fake model; user text never lands in the system message.
- `SystemPromptTests` — invariants that must survive prompt edits.
- `LiveModelEvaluationTests` — adversarial prompts against a real provider ([ai-evaluation.md](ai-evaluation.md)).
