# LLM integration

## What problem it solves

The assistant needs to talk to different model providers (hosted OpenAI, local Ollama) without the rest of the
application caring which one is used, and it must stream partial answers so the user does not stare at a spinner.

## How it works

```
POST /api/chat/stream {conversationId?, message, clientMessageId?}
   │  rate limit (per user) → authentication → FluentValidation (non-blank, ≤ 4000 chars)
   ▼
AiChatService.PrepareTurnAsync            (everything that can fail "normally" happens here, before streaming starts)
   1. require the AiProcessing consent                      → 403 consent_required
   2. load the conversation WHERE Id AND UserId = caller    → 404 (also for someone else's)
   3. reject a repeated clientMessageId                     → 409 duplicate_message
   4. load recent history (≤ 20 messages and ≤ 12,000 characters, newest first, failed answers skipped)
   5. EmergencyDetector.Assess(message)                     (see ai-safety.md)
      (the user's local time from the browser is kept for the prompt: "today" is resolved by the client, not guessed)
   6. persist the user message (+ audit event)              → the user's words are never lost
   ▼
AiChatService.StreamAsync  →  Server-Sent Events:  conversation, [safety], delta*, done | error
   • emergency → fixed answer, persisted, model NOT called
   • otherwise → FunctionInvokingChatClient(IChatClient).GetStreamingResponseAsync([system, ...history, user], options + TurnTools)
                 the model may call tools (events "tool" and "proposal" are streamed; see tool-calling.md)
                 accumulate text, emit delta events, then persist the assistant message:
                 Complete | Failed (provider error/timeout/empty answer, partial text kept) | Interrupted (user stopped)
```

Key code: `Modules.Assistant/Providers/ChatClientFactory.cs` (providers), `Features/AiChatService.cs` (orchestration),
`AssistantModule.cs` (SSE endpoint), frontend `features/chat/stream.ts` + `ChatPage.tsx`.

### Provider abstraction

`Microsoft.Extensions.AI.IChatClient` is the only AI type the application uses. `ChatClientFactory.Create` builds one from
`AiOptions`:

| `Ai:Provider` | Implementation | Required settings |
|---|---|---|
| `OpenAI` | `OpenAIClient(...).GetChatClient(model).AsIChatClient()` | `Ai:OpenAI:ApiKey`, `Ai:OpenAI:Model`; optional `Ai:OpenAI:Endpoint` for compatible gateways |
| `Ollama` | `OllamaApiClient` (implements `IChatClient`) | `Ai:Ollama:Endpoint`, `Ai:Ollama:Model` |

Model names, endpoints and keys are configuration only. A missing setting does not stop the application: the singleton
factory throws `ServiceUnavailableException("ai_not_configured")`, shown to the user as "service not available".

### Streaming over SSE

The endpoint returns `text/event-stream` through `TypedResults.ServerSentEvents`. Each event has a name and a JSON
payload (`ChatStreamEvent`). Because the HTTP status is committed when streaming starts, **every pre-flight problem is
raised before the first byte** (`PrepareTurnAsync`), and failures *after* that are reported as an `error` event with a
stable code (`ai_unavailable`, `ai_timeout`, `ai_empty_response`). Provider messages, URLs or keys are never forwarded.

The browser uses `fetch` + `ReadableStream` rather than `EventSource` because it needs an `Authorization` header and a POST body.

### Persistence and conversation memory

`Conversations` and `ChatMessages` store the history; the conversation is the only "memory" in Phase 2. Each turn re-sends
a bounded window of recent messages (count and characters), which keeps cost predictable and limits what leaves the
system. Long-term memory (summaries, user facts) is future work, see [rag-embeddings-memory.md](rag-embeddings-memory.md).

Reliability details: messages carry a per-conversation `Sequence` (unique) for ordering; a client-generated
`ClientMessageId` (unique per conversation) makes a retried send idempotent; the assistant message is persisted in a
`finally` block with a fresh cancellation token so a closed browser tab still leaves a consistent history (`Interrupted`).

### Tools (Phase 3)

Unless `Ai:ToolsEnabled=false`, the client is wrapped per turn in `FunctionInvokingChatClient` with four tools bound to the caller (see
[tool-calling.md](tool-calling.md)). The stream gains `tool` (running/done/failed) and `proposal` (a meal card awaiting confirmation) events. If the model
answers a meal with only a tool call, the service adds one deterministic sentence so the answer is never empty while a card is on screen.

### Generation options

`MaxOutputTokens`, `Temperature` (default 0.3, calmer answers for health topics), history limits and the request
timeout are configuration.

## Why this implementation

- A single orchestrating service class is enough for one assistant; it keeps the control flow readable end to end.
- `IChatClient` gives uniform streaming, tool and structured-output support later, without provider types leaking.
- Persisting the user message **before** calling the model and the assistant message **always** (even on failure) makes
  failures visible and debuggable instead of silently losing turns.

## Limitations

- Only the OpenAI and Ollama adapters exist; tool calling and structured output differ per model and need per-model tests.
- No token accounting or cost budget per user yet (only a per-user request rate limit and an output token cap).
- The assistant cannot see the user's data (no tools yet) and says so in its system prompt.
- The OpenAI SDK retries failing calls itself, so an outage delays the error by a few seconds.
- Only the mock-server path was run end-to-end for OpenAI; real OpenAI and real Ollama runs are still to be done by the operator.
- Conversations are never summarised: very long chats lose their earliest context.

## How it is tested

- `tests/IntegrationTests/ChatTests.cs` (real API, real SQL Server, **scripted fake `IChatClient`**): consent gate,
  streaming order and persistence, system prompt first and history order, user text never in the system message,
  emergency bypass, provider failure without leaked details, partial answer then failure, empty answer, failed answers
  not replayed, cross-user access, duplicate client id, validation limits, deletion, export, account erasure.
- `tests/UnitTests/Assistant`: factory configuration errors (never echoing keys), validators, prompt invariants.
- `src/frontend/.../chat/Chat.test.tsx`: SSE parsing (including events split across chunks), streaming UI, markdown, error and consent states, no HTML injection.
- Manual: the real OpenAI SDK path against a local OpenAI-compatible mock streaming server.
