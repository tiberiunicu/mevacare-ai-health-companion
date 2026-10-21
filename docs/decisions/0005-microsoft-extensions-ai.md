# ADR-0005: `IChatClient` (Microsoft.Extensions.AI) with OpenAI and Ollama providers

**Status:** Accepted

**Context.** The assistant must work with a hosted model (OpenAI) and a local model (Ollama), selected by
configuration, with provider specifics isolated and no hard-coded model names or keys.

**Decision.** The application depends only on `Microsoft.Extensions.AI.IChatClient`. `ChatClientFactory` is the single
place that references provider SDKs: `Microsoft.Extensions.AI.OpenAI` (an adapter over the OpenAI SDK) and `OllamaSharp`
(which implements `IChatClient`). The client is a lazily created singleton, so a missing provider configuration
produces HTTP 503 `ai_not_configured` for chat only. Model, endpoint, key, limits and timeout are options. Streaming is
the primary call (`GetStreamingResponseAsync`).

**Alternatives.** Semantic Kernel (a larger framework than needed for one assistant with a few tools) and raw provider
SDKs (provider types would leak into the application).

**Consequences.** Tools, structured output and embeddings in later phases use the same abstractions
(`FunctionInvokingChatClient`, `GetResponseAsync<T>`, `IEmbeddingGenerator`). Provider differences in tool-calling or
JSON-mode support must be tested per model. The OpenAI SDK retries failed calls by default, so a provider outage costs
a few seconds before the user sees the error.
