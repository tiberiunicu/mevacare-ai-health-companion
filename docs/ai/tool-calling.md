# Tool calling (function calling)

> **Status: implemented (Phase 3, extended in Phases 4 and 5).** Eight tools.

## What problem it solves

The model cannot know the user's meals or profile and must not guess them. Tool calling lets it *ask* the backend, which
answers from verified relational data. It also lets the model *prepare* an action (log a meal) that the user must confirm.

## How it works here

Code: `Modules.Assistant/Tools/TurnTools.cs`, `Features/AiChatService.cs`, `Features/Proposals.cs`; contracts in
`Contracts/Nutrition/NutritionContracts.cs`.

```
user message ──► AiChatService ──► FunctionInvokingChatClient(IChatClient)  ◄── tools: TurnTools (bound to THIS user)
                                      │  model asks: PrepareMeal("I ate 200 g chicken breast and 150 g cooked rice")
                                      ▼
                         TurnTools.PrepareMeal  ──► MealParser (2nd LLM call, structured output, see structured-outputs.md)
                                      │            ──► INutritionAssistantApi.PrepareMealAsync  (deterministic food match + math)
                                      │            ──► ToolExecutions row (state Proposed, the proposal JSON) + SSE "proposal" event
                                      ▼
                         tool result to the model: items, grams, kcal, totals, warnings, "saved": false
                                      ▼
                         model writes a short answer; the UI shows the proposal card; the USER confirms in the card
                         ──► POST /api/nutrition/meals { proposalId, items }  (normal authenticated API, not a tool)
```

| Tool | Kind | Purpose |
|---|---|---|
| `GetHealthProfile()` | Read | age (derived, never the date of birth), sex, height, activity level, goal types |
| `GetDailyNutritionSummary(date)` | Read | totals, targets and remaining for one local day, plus meals (name, grams, kcal) |
| `GetNutritionSummary(from, to)` | Read | per-day totals and the average over *logged* days (≤ 366 days) |
| `PrepareMeal(description)` | Write (proposal only) | extract (including a named day such as "yesterday", resolved against the user's local date), match, calculate; **writes nothing**; the user saves the card with one tap ([ADR-0017](../decisions/0017-chat-first-actions.md)) |
| `GetWeightHistory(from, to)` | Read | weight entries between two dates (≤ 366 days), latest value and change; the model sees at most the last 60 points and the source of each |
| `GetSleepAndRecovery(from, to)` | Read | per day: time asleep, deep/REM, sleep performance, naps, recovery score, resting heart rate, HRV, with the provider named, plus per-provider averages (last 31 days shown); the result tells the model that scores are the provider's own and must not be compared across providers or turned into medical conclusions |
| `GetHealthInsights(days, endDate)` | Read | trends, comparison with the previous period and combined insights calculated by the application (7–90 days); the result tells the model to explain only these numbers, as associations, with the listed caveats. See [insight-explanations.md](insight-explanations.md) |
| `ProposeWeight(kilograms, date?)` | Write (proposal only) | validates 20-500 kg and the day; shows a card; **stores nothing** until the user confirms |
| `ProposeNutritionTargets(calories?, proteinG?, ...)` | Write (proposal only) | merges with the current targets (unmentioned values stay); card shows before and after; a change that changes nothing is rejected |
| `ProposeProfileUpdate(heightCm?, activityLevel?)` | Write (proposal only) | height 50-260 cm and a known activity level; nothing else in the profile can be changed |
| `ProposeMealDeletion(mealId)` | Write (proposal only) | the meal must be the user's own; card names it and its foods |
| `PrepareMeal(description, replacesMealId?)` | Write (proposal only) | with `replacesMealId` the card corrects that meal instead of adding one |
| `GetWearableStatus()` | Read | connected accounts and their last synchronization |
| `SyncWearable(provider)` | Write (runs at once) | manual synchronization of an account the user connected; refused when not connected or too soon after the last run |
| `SearchMedicalKnowledge(query)` | Read | searches the approved reference documents (not the user's data); returns numbered passages labelled as data, to be cited with `[n]`; query ≤ 1,000 characters; registered only if `Knowledge:Enabled`. See [rag-embeddings-memory.md](rag-embeddings-memory.md) |

### The rules and where they are enforced

| Rule | Enforcement |
|---|---|
| The user id comes from the server | `TurnTools` is constructed per turn with the authenticated user id; **no tool has a user-id parameter** (a test inspects the JSON schemas and a hijacked model passing `userId` still reads only the caller's data) |
| Authorization and consent | each tool calls a service that re-checks `HealthDataProcessing` and filters by user id; a missing consent returns `{ error, code: "consent_required" }` to the model |
| Read vs write | `ToolKind` is recorded; the only write-type tool just stores a proposal |
| Explicit confirmation | a meal is created only by `POST /api/nutrition/meals` with `proposalId`, by the signed-in user; the system prompt also forbids saying it was saved |
| Numbers in a prepared change | `ProposeWeight`, `ProposeNutritionTargets` and `ProposeProfileUpdate` accept only numbers that appear in the user's **latest message** (`TurnTools.EnsureStated`; decimal comma or point and thousands separators are understood). An invented, calculated or spelled-out number is refused with `value_not_stated` and the model must ask. Found by the live tool-selection evaluation, which saw the model invent a calorie target; tested |
| Prepared changes (weight, targets, profile, meal deletion) | the tool stores a typed payload (`ToolExecutions`, state `Proposed`) and streams an `action` event; only `POST /api/assistant/actions/{id}/confirm` by the signed-in user applies **that stored payload**, once (conditional UPDATE claims it), through the owning module (ADR-0017). Tests cover double and parallel confirmation, dismissal, another user's card, missing consent and a failed apply releasing the claim |
| Duplicate writes on retry | `Meals.SourceProposalId` has a unique index, and `ProposalGate` marks the proposal committed in the **same transaction**; confirming twice (or three parallel requests) returns the same meal (tested) |
| Argument validation | dates must be `YYYY-MM-DD`; the meal description is limited to 1,000 characters; extraction is validated (≤ 15 items, quantity 0–10,000, unit present) and then re-validated by the nutrition module; failures become `Rejected` executions and a small error object for the model, never an exception or a stack trace |
| Unknown tools | the model can only call registered functions; a request for `DeleteAllMeals` is not executed (tested) |
| Loop protection | `MaximumIterationsPerRequest` (config `Ai:MaxToolIterations`, default 6) and the request timeout |
| Audit | every call is stored in `assistant.ToolExecutions` (tool, arguments, outcome, error code, duration). Only proposals keep their result JSON (the data the user confirms); read-tool results are **not** stored. |
| Tool results are data | the system prompt says so; results contain only numbers and food names |
| The LLM is not a calculator | calories and macros come from `NutrientCalculator`; the prompt forbids estimating them |
| Turn tools off | `Ai:ToolsEnabled=false` for models without tool support; the prompt then says the assistant cannot see personal data |

## Limitations

- Small local models call tools unreliably or not at all; test the chosen model with the live evaluation.
- Tool use costs extra model round trips (the meal flow makes three calls), latency and tokens.
- Tool results are not replayed in later turns; the model sees only earlier user/assistant text.
- `GetHealthProfile` shares age, sex, height and activity level with the AI provider once the user consented to both health-data and AI processing.
- Only the OpenAI-compatible path was exercised end to end (against a local mock server that emits tool calls and JSON-schema answers); real OpenAI/Ollama tool behaviour is unverified.

## How it is tested

`tests/IntegrationTests/ChatToolTests.cs` runs the real API and SQL Server with a **scripted model** (tool call → extraction JSON →
final text): the chicken-and-rice definition of done, the numbers the model receives, confirm-twice idempotency (including parallel),
corrections before confirmation, dismissal, ownership of proposals, reload, unknown foods, household measures without a known
portion, raw/cooked assumptions, malformed and hostile extraction, consent, invalid dates, `userId` injection attempts,
unknown tool names, runaway loops, and emergency messages bypassing everything.
