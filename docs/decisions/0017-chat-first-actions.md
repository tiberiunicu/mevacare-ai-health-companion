# ADR-0017: Chat-first actions: one assistant panel on every page; the user still saves with one tap

**Status:** Accepted and built: meals (one-tap Save), then weight, nutrition targets, profile basics, correcting and deleting meals, WHOOP status and sync.

**Context.** The owner wants to do most things by talking: "I had two eggs and toast" instead of searching a list, and the same for
other actions. The first assistant (ADR-0009) already turned a sentence into a meal proposal, but it lived on its own page, needed two
steps (*Review and confirm*, then *Confirm and save*), knew nothing about days ("yesterday at dinner" was saved as now) and the food
journal only offered the list-based editor.

**Decision.**
- **One conversation, available everywhere.** The chat state moved above the router (`ChatProvider`). A floating button opens a
  side panel (`ChatDock`) on every page except the full chat page, which shows the same conversation. The food journal has a
  "What did you eat?" box: the sentence is handed to the assistant (`useChat().ask`), which opens the panel and answers there.
- **The principle of ADR-0009 stays:** the model prepares, the user's own authenticated request saves. A chat sentence is still not a
  confirmation. What changed is the effort: when every line of a proposal has a food and a weight, the card has a **Save** button
  (one tap, the same `POST /api/nutrition/meals` with the proposal id, so the server recalculates, validates and stays idempotent);
  **Edit** opens the editor as before; a card with an unmatched food or a missing weight offers only the editor.
- **Day in the sentence.** The extraction (`MealParser`) now also returns `eatenOn` and is told today's date (the user's own local
  date, sent by the browser with every message). The server accepts it only as a real date between today and 60 days ago; anything
  else means "now". The card shows "For 2026-11-09" and saves at the usual hour of the meal (breakfast 08:00, lunch 13:00,
  dinner 19:00, snack 16:00, noon when no meal was named); the editor can change it.
- **No list-picking in the conversation.** The prompt tells the model not to ask which food to choose: the card lets the user change anything.

**Alternatives.** Auto-saving meals with an *Undo* (faster, but the model would write into the journal without a user action, which
changes ADR-0009); a chat bar on every page with its own conversation (several histories, nothing shared); keeping the separate chat page only.

**Consequences.**
- Logging a meal is: type a sentence, check the card, tap Save. Two model calls as before; the proposal is still stored and audited.
- Reloading a conversation restores cards and their state (Proposed / Saved / Dismissed); the panel and the page show the same messages.
- A wrong day is possible when the model misreads a relative date; it is visible on the card before saving.
- The same card pattern now covers the other actions (see below), so the pages stay for browsing and for what chat deliberately does not do.

**Other actions (built).** They follow the same rule with a generalized proposal:
- **Write tools only prepare.** `ProposeWeight`, `ProposeNutritionTargets`, `ProposeProfileUpdate` and `ProposeMealDeletion` validate their arguments (the same limits as the pages: weight 20-500 kg and not in the future or older than five years; each target within its range; height 50-260 cm; a known activity level; a meal id that belongs to the user), store a typed payload in `ToolExecutions` and stream an `action` event with a card. Changing targets keeps every value the user did not mention (the card shows before and after); a change that changes nothing is rejected.
- **The user applies exactly that payload.** `POST /api/assistant/actions/{id}/confirm` claims the proposal with one conditional UPDATE (Proposed to Committed), so a double click or parallel requests apply it once, then calls the owning module through a contract interface (`IMeasurementsAssistantApi.RecordWeightAsync`, `INutritionAssistantApi.SetTargetsAsync` / `DeleteMealAsync`, `IProfileAssistantApi.SetBasicsAsync`). If applying fails the claim is released, so the card can be confirmed again. Dismissed cards cannot be confirmed; applied cards cannot be dismissed; another user's card is a 404. Consents are re-checked by the module at that moment.
- **Correcting a meal** reuses the meal card: `PrepareMeal(description, replacesMealId)` (the id comes from `GetDailyNutritionSummary`, which now lists meal ids) produces a proposal with `replacesMealId`; Save sends `PUT /api/nutrition/meals/{id}` with the proposal id, which `MealService.UpdateAsync` checks and marks committed in the same transaction (saving twice changes nothing). The card keeps the old meal's type, time and notes.
- **Wearables:** `GetWearableStatus` is a read. `SyncWearable` runs a manual synchronization directly, with no card: it is idempotent, imports only the user's own data from an account they already connected, and the integrations module keeps the consent checks, the per-account lock and the minimum interval between manual runs. Connecting or disconnecting an account (OAuth) is never offered to the assistant.
- **Deliberately not offered:** changing date of birth, sex or time zone, health goals, preferences, consents, deleting weight entries or the account. They remain on their pages.
- The assistant never has a tool that applies a change, and no tool takes a user id (a test checks both).

**Consequences of the other actions.** Each prepared change costs one model call plus the tool; a card is one tap. The model may misunderstand a value, which is why every card shows exactly what will change before anything does. The prompt tells the model to say a change was *prepared*, never *made*.
