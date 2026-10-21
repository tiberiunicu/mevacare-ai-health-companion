# AI engineering guide

This folder explains each AI component **in terms of the code that exists**, not generic theory. Every page answers:
what problem it solves, why it is needed, how it works here, why this implementation, its limits, and how it is tested.

| Component | Status | Page |
|---|---|---|
| LLM integration (`IChatClient`, providers, streaming, history, persistence) | **Implemented (Phase 2)** | [llm-integration.md](llm-integration.md) |
| Prompt engineering and AI safety (system prompt, emergency screen, injection handling) | **Implemented (Phase 2)** | [ai-safety.md](ai-safety.md) |
| AI evaluation (deterministic emergency evaluation, live adversarial evaluation) | **Implemented (Phase 2)** | [ai-evaluation.md](ai-evaluation.md) |
| Function calling / tools | Designed, **not implemented** (Phase 3) | [tool-calling.md](tool-calling.md) |
| Structured outputs | Designed, **not implemented** (Phase 3) | [structured-outputs.md](structured-outputs.md) |
| Embeddings, vector search, RAG, citations, AI memory | Designed, **not implemented** (Phase 4+) | [rag-embeddings-memory.md](rag-embeddings-memory.md) |

Pages marked *designed* describe the intended approach so later phases stay consistent; they must be revisited and
checked against the then-current package APIs when implemented.

## Reading order for a .NET developer new to AI engineering

1. `Modules.Assistant/Providers/ChatClientFactory.cs` — the whole provider story in 60 lines.
2. `Modules.Assistant/Features/AiChatService.cs` — one turn from request to persisted answer, including failure paths.
3. `Modules.Assistant/Safety/` — the system prompt and the deterministic screen.
4. `tests/IntegrationTests/ChatTests.cs` + `Support/ScriptedChatClient.cs` — how to test LLM code without an LLM.
5. `tests/AIEvaluationTests` — how to measure behaviour that unit tests cannot pin down.

## Principles used across the project

- **The LLM is never the source of truth** for facts the application owns (user data, nutrient values, measurements).
  It proposes; verified code computes and persists.
- **Identity and authorization come from the server**, never from model output.
- **Safety-critical behaviour is deterministic** where possible.
- **Everything the model sees is deliberate**: system prompt + bounded history + the new message. Nothing else.
- **Test the application around the model with a fake model; evaluate the model separately and honestly.**
