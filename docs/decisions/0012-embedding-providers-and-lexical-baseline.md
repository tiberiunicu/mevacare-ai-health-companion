# ADR-0012: Pluggable embedding providers with an offline lexical baseline, calibrated by evaluation

**Status:** Accepted

**Context.** Semantic search needs an embedding model. The project must run and be tested without an API key or a local model server, the two real options (OpenAI, Ollama) cost money or setup, and neither was available in the authoring environment.
Similarity scores are not comparable between models, so a fixed threshold copied from elsewhere would be wrong.

**Decision.**
- Embeddings go through `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI) wrapped by `IEmbeddingService` (batching, dimension check). Providers: **Lexical**, **OpenAI**, **Ollama**, chosen by `Knowledge:Embeddings:Provider`.
- **Lexical** is an offline feature-hashing embedding of words and bigrams (signed FNV-1a into a fixed number of dimensions, L2-normalised). It is deterministic, free and needs no network, so CI and a fresh clone can run the whole pipeline.
  It is explicitly a **baseline**: it matches shared words, not meaning, and the documentation says so wherever retrieval is described.
- The model identity `provider/model/dimensions` is stored per source. Searching with a different model than the one used for indexing is refused with `knowledge_reindex_required`; `reindex-knowledge --recreate` re-embeds everything from SQL.
- `Retrieval:MinScore` (default 0.15) was calibrated for the Lexical baseline on a synthetic corpus and five real pages. It is **model-specific configuration**; switching provider requires re-running the retrieval evaluation (recall@k, MRR, source-hit, false positives on unanswerable questions) and adjusting it.
- Retrieval quality is a tested property: synthetic gold set with enforced floors in CI, optional real-corpus run (`MEVACARE_KNOWLEDGE_DIR`) that prints every question with its hits.

**Alternatives.** Shipping only OpenAI/Ollama (no offline path: untestable here, key required to run anything); a bundled ONNX sentence-embedding model (download of tens of MB of third-party weights, new runtime dependency, needs approval and licence review);
TF-IDF/BM25 only (no vector index, which the architecture requires); a hard-coded threshold (silently wrong after a model change).

**Consequences.** Out of the box, retrieval misses synonyms and very short queries (measured on the sample corpus: recall@3 83 %, MRR 0.74, two misses). OpenAI/Ollama embeddings are implemented but **were not run against a real service**, so their quality and their `MinScore` are unmeasured —
this is the first thing to verify when a key or Ollama is available. Changing the embedding model is a deliberate, visible operation. Dimension 1024 is the default for all providers (OpenAI models accept a `dimensions` parameter; for Ollama it must match the model).
