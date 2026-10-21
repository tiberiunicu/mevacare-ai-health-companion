# ADR-0009: Tool calling with per-turn tools; AI prepares, the user confirms

**Status:** Accepted

**Context.** The assistant must read the user's data (summaries, profile) and help log meals, without the model ever
choosing whose data to access, writing data on its own, or acting as the authority for numbers.

**Decision.**
1. Tools are plain methods registered with `AIFunctionFactory` and executed by `FunctionInvokingChatClient`. A new `TurnTools` instance is created for every chat turn and is bound to the authenticated
   user's id; no tool has a user-id parameter.
2. Reads go through services that re-check consent and filter by user id. Only compact results are returned to the model.
3. Meal logging is split in two. `PrepareMeal` (a *write-kind* tool) extracts food items with structured output, matches and calculates deterministically, stores a **proposal** (`ToolExecutions`,
   state `Proposed`) and shows it as a card. Persisting a meal is a separate, normal API call made by the user (`POST /api/nutrition/meals` with `proposalId`), after review and optional correction.
4. Idempotency is enforced by the database: `Meals.SourceProposalId` is unique and the proposal is marked committed in the same transaction (`IMealProposalGate`, implemented by the Assistant module,
   called by the Nutrition module). Repeated or parallel confirmations return the same meal.
5. Every tool call is audited in `ToolExecutions`; failures are returned to the model as small error objects.
6. Cross-module needs go through `Contracts` interfaces (`INutritionAssistantApi`, `IHealthProfileReader`, `IMealProposalGate`), not through project references between modules.

**Why not let the model call a "SaveMeal" tool after asking the user in chat?** A chat sentence is not a reliable confirmation (the model may skip it, or be talked into it), and a retried tool
call would duplicate data. A UI action by the signed-in user is unambiguous and testable.

**Consequences.** Safer but one more step for the user. Three model round trips per meal. Proposals live in the Assistant schema and are deleted with the conversation (meals are not).
`ToolExecutions.UserId` is a plain column because a second cascade path from `Users` is not allowed by SQL Server.
