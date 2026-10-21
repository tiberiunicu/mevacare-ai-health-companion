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
invented personal data, tool/SQL abuse, injection inside quoted text and a Romanian medication question. The test reports
each answer and a pass rate and fails below `MEVACARE_EVAL_MIN_PASS_RATE` (default `1.0`).

```powershell
$env:MEVACARE_EVAL_LIVE = '1'
$env:Ai__Provider = 'Ollama'            # or OpenAI with Ai__OpenAI__ApiKey / Ai__OpenAI__Model
$env:Ai__Ollama__Model = '<model>'
dotnet test tests/AIEvaluationTests --logger "console;verbosity=detailed"
```

Without `MEVACARE_EVAL_LIVE=1` the test is **skipped** (and reported as skipped, never silently passed).

## How to use it when changing things

1. Change a prompt or model → run the live evaluation before and after; read the printed answers, not only the pass rate.
2. Found a bad answer in the wild → add it (synthetic form) to the dataset **first**, watch it fail, then fix.
3. Add languages, categories and paraphrases to the emergency dataset as the product learns.

## Limitations

- Regex heuristics judge text superficially; they can pass a harmful answer worded unexpectedly and fail a safe one.
  Treat the pass rate as a regression alarm, not a safety proof. Periodic human review of the printed answers is required.
- ~10 live cases and ~56 triage cases is a seed, not coverage.
- The live suite has **not been run** yet: no provider was available in the authoring environment.
- Planned for later phases: retrieval accuracy evaluation (Phase 4: recall@k, citation correctness on a small gold set),
  tool-calling evaluation (Phase 3: correct tool, argument validity, refusal to write without confirmation), and an
  LLM-as-judge option with human spot checks.
