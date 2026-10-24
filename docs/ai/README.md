# AI engineering guide

This folder explains each AI component **in terms of the code that exists**, not generic theory. Every page answers:
what problem it solves, why it is needed, how it works here, why this implementation, its limits, and how it is tested.

| Component | Status | Page |
|---|---|---|
| LLM integration (`IChatClient`, providers, streaming, history, persistence) | **Implemented (Phase 2)** | [llm-integration.md](llm-integration.md) |
| Prompt engineering and AI safety (system prompt, emergency screen, injection handling) | **Implemented (Phase 2)** | [ai-safety.md](ai-safety.md) |
| AI evaluation (deterministic emergency evaluation, live adversarial evaluation) | **Implemented (Phase 2)** | [ai-evaluation.md](ai-evaluation.md) |
| Function calling / tools (seven tools, including `SearchMedicalKnowledge` and the measurement reads) | **Implemented (Phases 3–5)** | [tool-calling.md](tool-calling.md) |
| Structured outputs (meal extraction) | **Implemented (Phase 3)** | [structured-outputs.md](structured-outputs.md) |
| Embeddings, vector search, RAG, citations, retrieval evaluation | **Implemented (Phase 4)**; real embedding providers never run | [rag-embeddings-memory.md](rag-embeddings-memory.md) |
| AI memory | Designed, **not implemented** | [rag-embeddings-memory.md](rag-embeddings-memory.md#ai-memory-later--designed-not-implemented) |

Pages or sections marked *designed* (currently only AI memory in [RAG, embeddings and memory](rag-embeddings-memory.md)) describe the intended approach so later phases stay consistent; they must be revisited and
checked against the then-current package APIs when implemented.

## Reading order for a .NET developer new to AI engineering

1. `Modules.Assistant/Providers/ChatClientFactory.cs` — the whole provider story in 60 lines.
2. `Modules.Assistant/Features/AiChatService.cs` — one turn from request to persisted answer, including failure paths.
3. `Modules.Assistant/Safety/` — the system prompt and the deterministic screen.
4. `Modules.Assistant/Tools/` — per-turn tools and structured extraction; `tests/IntegrationTests/ChatTests.cs`, `ChatToolTests.cs` + `Support/ScriptedChatClient.cs` — how to test LLM code (including tool calls) without an LLM.
5. `Modules.Knowledge/` — ingestion → chunking → embeddings → vector store → retrieval, then `Tools/TurnTools.cs` (`SearchMedicalKnowledge`) for how retrieved text reaches the model.
6. `tests/AIEvaluationTests` — how to measure behaviour that unit tests cannot pin down (including retrieval recall).

## Principles used across the project

- **The LLM is never the source of truth** for facts the application owns (user data, nutrient values, measurements).
  It proposes; verified code computes and persists.
- **Identity and authorization come from the server**, never from model output.
- **Safety-critical behaviour is deterministic** where possible.
- **Everything the model sees is deliberate**: system prompt + bounded history + the new message. Nothing else.
- **Test the application around the model with a fake model; evaluate the model separately and honestly.**
