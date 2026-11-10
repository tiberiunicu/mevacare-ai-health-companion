# ADR-0017: Chat-first actions: one assistant panel on every page; the user still saves with one tap

**Status:** Accepted for meals (built). Accepted as a plan for the other actions (not built, see *Next*).

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
- Cards exist only for meals so far. Edits and deletions of past meals, weight, targets, profile and WHOOP actions still need their pages.

**Next (planned, not built).** Other actions follow the same rule with a generalized proposal: write tools (`ProposeWeight`,
`ProposeNutritionTargets`, `ProposeProfileUpdate`, meal change and deletion that identify a meal by id) validate the arguments, store a
typed proposal and show a card; a new `POST /api/assistant/actions/{id}/confirm` applies exactly the stored payload through contract
interfaces of the owning module, in one transaction, idempotently. The model never supplies the final payload after the user has seen it.
WHOOP status and "synchronize now" are low-risk and idempotent and could run directly. Connecting an account (OAuth) stays on its page.
