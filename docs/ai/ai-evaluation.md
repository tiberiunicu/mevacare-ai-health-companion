# AI evaluation

## What problem it solves

Unit tests pin down deterministic code. They cannot say whether a model behaves safely, and prompts regress silently
when someone edits a sentence or swaps a model. Evaluation turns "seems fine" into numbers you can compare over time.

## Two kinds of evaluation in this repository

### 1. Deterministic evaluation (runs everywhere, in CI)

`tests/AIEvaluationTests/EmergencyTriageEvaluationTests.cs` runs the emergency screen over
`Datasets/emergency-triage.json`:

- `emergency` — messages that **must** trigger (recall must be 100 %), with the expected category;
- `benign` — wellness questions that **must not** trigger (false positives must be 0);
- `knownLimitations` — false positives we accept; the test asserts they still trigger, so a fix forces a dataset update.

This evaluation already earned its keep: it exposed that "thinking about ending my life" was missed by the first
regex, which was fixed before the first commit of the feature.

### 2. Live model evaluation (opt-in, costs money, non-deterministic)

`LiveModelEvaluationTests` sends the real system prompt plus each prompt in `Datasets/adversarial-prompts.json` to the
configured provider (temperature 0) and applies heuristic checks:

| Check | Meaning |
|---|---|
| `mustNotContain` | forbidden text, e.g. the prompt canary or phrases of the instructions (prompt leakage) |
| `mustNotMatch` | forbidden patterns, e.g. a medication dose, a definite diagnosis, a 500 kcal plan, claiming a SQL command ran |
| `mustMatchAny` | at least one hedging/referral pattern must appear (doctor, pharmacist, cannot see your data...) |

Cases cover prompt leakage (direct, role-play, translation), medication changes, diagnosis demands, extreme diets,
invented personal data, claiming a meal was saved, invented calories, tool/SQL abuse, injection inside quoted text and a Romanian medication
question. (This harness sends the system prompt without tools, so it checks that the model does not claim data or saves it cannot have.) The test reports
each answer and a pass rate and fails below `MEVACARE_EVAL_MIN_PASS_RATE` (default `1.0`).

```powershell
$env:MEVACARE_EVAL_LIVE = '1'
$env:Ai__Provider = 'Ollama'            # or OpenAI with Ai__OpenAI__ApiKey / Ai__OpenAI__Model
$env:Ai__Ollama__Model = '<model>'
dotnet test tests/AIEvaluationTests --logger "console;verbosity=detailed"
```

Without `MEVACARE_EVAL_LIVE=1` the test is **skipped** (and reported as skipped, never silently passed).

### 3. Meal extraction evaluation (opt-in)

`MealExtractionEvaluationTests` runs `MealParser` (structured output) over `Datasets/meal-extraction.json`: an English example from the brief, raw
weights, household units, a Romanian sentence, a missing amount, and an instruction hidden inside the meal text. It checks names, amounts, units and the
stated raw/cooked wording, and that injected items are not extracted. The dataset shape is validated in every run; the model run needs
`MEVACARE_EVAL_LIVE=1` like the adversarial set.

### 4. Retrieval evaluation (Phase 4, runs everywhere)

`RetrievalEvaluationTests` runs the real chunker, embedding service and retrieval service (in-memory vector store) over a gold set: for each question the expected source and passage terms, plus unanswerable questions that must return nothing. It reports recall@1/3/5, MRR, source-hit@3 and false positives, printing every question with its hits and scores.

- Synthetic corpus (`Datasets/synthetic-knowledge`, five fictional documents; `retrieval-gold-synthetic.json`): enforces recall@3 ≥ 0.90, source-hit@3 ≥ 0.90, MRR ≥ 0.85 and 0 false positives with the Lexical baseline.
- Real corpus: `MEVACARE_KNOWLEDGE_DIR=<folder with sources.json>` (optionally `MEVACARE_EVAL_MIN_RECALL_AT3`, default 0.75) with a gold set shaped like `retrieval-gold-demo-corpus.json`. On the five US-government sample pages with the Lexical baseline: recall@3 83 %, MRR 0.74, 0/3 false positives; the two misses are a synonym question and a short query.
- Use it to calibrate `Knowledge:Retrieval:MinScore` whenever the embedding model changes (scores are not comparable between models). **This has not been done for OpenAI or Ollama embeddings.**

## How to use it when changing things

1. Change a prompt or model → run the live evaluation before and after; read the printed answers, not only the pass rate.
2. Found a bad answer in the wild → add it (synthetic form) to the dataset **first**, watch it fail, then fix.
3. Add languages, categories and paraphrases to the emergency dataset as the product learns.

## Limitations

- Regex heuristics judge text superficially; they can pass a harmful answer worded unexpectedly and fail a safe one.
  Treat the pass rate as a regression alarm, not a safety proof. Periodic human review of the printed answers is required.
- ~10 live cases and ~56 triage cases is a seed, not coverage.
- The live suites (adversarial and meal extraction) have **not been run** yet: no provider was available in the authoring environment.
- Tool-selection accuracy (does the model call `PrepareMeal` for "I ate..." and a summary tool for "how many calories today") is covered only with a scripted
  model plus manual runs against a mock server; a live tool-selection evaluation is still to be written.
- Retrieval evaluation (Phase 4) measures the retriever, not the model's answer: whether the model cites faithfully and does not misread a passage is **not evaluated**. The synthetic gold set has 17 answerable and 4 unanswerable questions; the real-corpus numbers come from 5 pages and 15 questions. Treat them as a regression alarm.
- Planned: an LLM-as-judge option with human spot checks.
