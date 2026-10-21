# Tool calling (function calling)

> **Status: designed, not implemented.** Planned for Phase 3. Nothing on this page exists in code yet; check the then-current
> `Microsoft.Extensions.AI` API (`AIFunctionFactory`, `FunctionInvokingChatClient`) before implementing.

## What problem it solves

The model cannot know the user's meals, weight or targets, and must not guess them. Tool calling lets it *ask* the
backend, which answers from verified relational data. It also lets the model *prepare* an action (log a meal) that
the user then confirms.

## Planned design

| Rule | Reason |
|---|---|
| Tools are plain C# methods registered with `AIFunctionFactory`; the loop is `FunctionInvokingChatClient` wrapped around the provider client | one mechanism for OpenAI and Ollama; adding a tool = adding a method + registration |
| The authenticated user id is captured from `ICurrentUser` when the turn starts and bound into the tool instance; **no tool has a user-id parameter** | the model can never address another user's data |
| Each tool declares `Read` or `Write` | writes get stricter handling |
| Arguments are deserialised into records and validated with FluentValidation; invalid arguments return a structured error to the model (it may retry once) and are recorded | models send malformed or hostile arguments |
| Read tools query the database with the same services the UI uses (`GetDailyNutritionSummary`, `GetNutritionSummary`, `GetWeightHistory`, `GetHealthProfile`) and return **minimal** DTOs | data minimisation toward the provider |
| Write tools never write on the model's say-so: `PrepareMeal` returns a *proposal* persisted as `ToolExecutions` with state `Proposed`; only the user's confirmation (a normal authenticated API call) changes it to `Committed` and creates the meal | explicit confirmation; the model is not the authority |
| Writes carry an idempotency key (execution id + unique index) | retries and double clicks cannot duplicate a meal |
| Nutrient values are computed by deterministic code from the food database, never by the LLM | the LLM is not a calculator |
| Every call is stored in `ToolExecutions` (tool, validated arguments, outcome, duration; no free-text health content beyond what the tool needs) | auditability and debugging |
| Tool results are data, not instructions: they are passed back as tool-role content and the system prompt says so | indirect prompt injection |

Initial tools from the brief: `GetHealthProfileAsync`, `GetDailyNutritionSummaryAsync(date)`,
`GetNutritionSummaryAsync(from, to)`, `PrepareMealAsync(description)`, `GetWeightHistoryAsync(from, to)`,
`SearchMedicalKnowledgeAsync(query)` (the last one arrives with RAG in Phase 4).

## Limitations to expect

Small local models call tools unreliably; some ignore the tool schema. Tool use adds latency and cost per round trip.
A confirmation step adds friction by design.

## How it will be tested

Fake `IChatClient` scripted to emit tool calls (valid, malformed, with a foreign user id, repeated) against the real
database: authorization, validation errors, no write without confirmation, idempotent commit, tool-call audit rows.
Live evaluation adds tool-selection accuracy and "refuses to write without confirmation".
