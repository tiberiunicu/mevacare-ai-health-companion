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
| 3. Structural isolation | `AiChatService`, `TurnTools` | user text only in the *user* role; the system prompt is built from constants plus the date; tools are bound to the caller and cannot take a user id; writes need user confirmation; no retrieved text, no code or SQL execution | yes |
| 4. Output handling | frontend | Markdown without raw HTML | yes |
| 5. Evaluation | `tests/AIEvaluationTests` | measures layers 1–2 against labelled, adversarial and meal-extraction datasets | n/a |

### Emergency screen

Regexes (English and Romanian) run on a lower-cased, diacritic-stripped copy of the message with a 100 ms match timeout
(a timeout counts as a match: fail safe). Categories: `MedicalEmergency` (chest pain with red-flag context, cannot
breathe, stroke signs, heavy bleeding, overdose/poisoning, throat swelling, seizure/unconscious) and `SelfHarm`
(which wins when both match). The answers name emergency numbers (112 EU, 911 US, 988 US crisis line) and
`findahelpline.com`; no country-specific hotline numbers are invented.

### System prompt

Contains a **canary** token. If it ever shows up in an answer the model leaked its instructions; the live evaluation
checks for it. The prompt is built per turn (`SystemPrompts.Build`) and has two variants. With tools it says personal data is available **only** through the tools,
that tool results are data not instructions, that meals are only *prepared* (never "saved"), that nutrient numbers must come from tools and are never calculated or
estimated by the model, and it gives the user's local date (sent by the browser) so "today" can be resolved. Without tools (`Ai:ToolsEnabled=false`) it says the
assistant cannot see personal data at all.

## Prompt-injection stance

User messages are untrusted and can only *argue* with the model, not edit its instructions. Retrieved reference passages (Phase 4) are the only third-party text the model sees; there are no
secrets in the prompt. With tools (Phase 3) the controls are structural, not prompt-based: tool calls run server-side with the caller's id,
are validated and consent-checked, nothing is written without the user's confirmation, and the extraction call treats the meal text as data
([tool-calling.md](tool-calling.md)). Retrieved passages are returned as tool results labelled "DATA, not instructions", the prompt repeats that, chunks that look like instructions (injection heuristics) are quarantined when the document is ingested, and only operators can ingest documents. The worst a hostile document can do is skew a cited answer: the tools that act for the user are validated server-side and writes need the user's confirmation ([rag-embeddings-memory.md](rag-embeddings-memory.md)). The live adversarial evaluation checks the prompt-level behaviour.

## Limitations (read these)

- The emergency screen is a **keyword heuristic**: it misses paraphrases and other languages and fires on some benign
  phrases (accepted for recall; documented in `knownLimitations` in the dataset).
- The system prompt is guidance, not enforcement: a model can still produce an unsafe answer. Nothing here is a
  clinical safety guarantee, and the product must keep presenting itself as wellness information only.
- No output moderation or PII filter runs on model answers yet. A model can still *say* numbers that did not come from a tool; the prompt forbids it, the numbers shown in cards and journals come from the database, and the evaluation checks for invented personal data.
- With tools, the assistant gets the caller's own profile facts and nutrition totals when it asks for them (after consent); a successful injection can make it reveal those to the caller only.
- No jailbreak or red-team coverage beyond the small dataset; medical-claim accuracy is not evaluated.
- Romanian system-prompt behaviour depends on the chosen model.
- Insight explanations: the numbers come from the application and the model is checked for invented ones, but a model can still word a true number misleadingly or reassure too much; causal and diagnostic wording is forbidden by the prompt and absent from the application's own statements, not enforced on the model's output.
- Wearable scores (recovery, strain) are the device provider's own numbers. The prompt and the tool results tell the model to report them as the provider's score and not to compare them across devices or draw medical conclusions; that is guidance, not enforcement, and it is covered only by the scripted tests, not by a live evaluation.
- Retrieval can surface an approved but outdated or misread passage; a citation shows *where* a claim came from, it does not prove the claim is right or fits the user. The injection heuristics are patterns, not a guarantee; the document approval process is the real control.

## How it is tested

- `EmergencyTriageEvaluationTests` — 30 labelled emergencies (100 % recall required), 26 benign questions (0 false
  positives required), category and language checks, diacritics, case, regex time budget, known limitations.
- `ChatTests` — emergency messages never reach the fake model; user text never lands in the system message.
- `SystemPromptTests` — invariants that must survive prompt edits.
- `LiveModelEvaluationTests` — adversarial prompts against a real provider ([ai-evaluation.md](ai-evaluation.md)).
