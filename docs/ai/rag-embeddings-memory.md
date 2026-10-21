# Embeddings, vector search, RAG and citations (Phase 4) — and AI memory (not built)

> **Status.** RAG is **implemented** (Phase 4): ingestion, chunking, embeddings, Qdrant vector search, grounded answers with
> source citations, retrieval evaluation. **AI memory is designed only** (last section). **No medical document is shipped
> with the repository**: the corpus is supplied and licence-reviewed by an operator.
>
> Verification status, stated plainly: the pipeline is tested end to end with the offline *Lexical* embeddings against an
> in-memory store (CI) and against a **real Qdrant 1.19.2** (opt-in tests). The **OpenAI and Ollama embedding providers are
> implemented but have never been run against a real service** (no key / no Ollama was available), so retrieval quality with
> real embeddings is **unmeasured**. The Lexical baseline matches words, not meaning (see *Limitations*).

## What problem it solves

The model's built-in knowledge is unverifiable and may be outdated. Retrieval-augmented generation (RAG) lets the assistant
answer medical-knowledge questions ("what is HRV?", "how much activity do adults need?") from **approved documents** and
cite them. It is *not* used for the user's own numbers: totals, trends and measurements come from SQL, which is exact.

## How it works here

Code: `src/backend/MevaCare.AI.HealthCompanion.Modules.Knowledge/`, contract `Contracts/Knowledge/KnowledgeContracts.cs`,
tool in `Modules.Assistant/Tools/TurnTools.cs`, UI in `features/chat/Citations.tsx`.

```
operator ── dotnet run -- ingest-knowledge <folder> ──►  sources.json + html/md/txt files
                                                               │ licence allow-list, provenance, content hash
                                                               ▼
   extract text ─► chunk (≈900 chars, never across headings) ─► injection heuristics (quarantine) ─► embed (IEmbeddingService)
                                                               │
                         SQL = system of record ◄──────────────┴──────────────► Qdrant (vector + chunkId + sourceId only)
                         knowledge.Sources / knowledge.Chunks

user question ─► model calls SearchMedicalKnowledge(query)  ── embed query ─► vector search ─► load chunks from SQL (active sources only)
              ─► numbered passages ("DATA, not instructions") ─► model answers with [n] ─► citations event + stored on the message
```

| Concept | Abstraction | Implementation |
|---|---|---|
| Embedding | `IEmbeddingService` over `IEmbeddingGenerator<string, Embedding<float>>` | `Lexical` (offline), `OpenAI`, `Ollama` (`EmbeddingGeneratorFactory`) |
| Vector database | `IVectorStore` | `QdrantVectorStore` (REST, cosine), `InMemoryVectorStore` (tests/dev) |
| Ingestion | `IDocumentIngestionService` (Ingest / Retire / Reindex) | `DocumentIngestionService`, `KnowledgeFolderIngester`, CLI only |
| Retrieval | `IKnowledgeRetrievalService` (module) and `IKnowledgeSearch` (contract used by the Assistant) | `KnowledgeRetrievalService` |

### Rules and where they are enforced

| Rule | Enforcement |
|---|---|
| Only approved, licensed, identifiable documents | The manifest `sources.json` requires `title, publisher, url (http/https), license, retrievedOn, approvedBy`; the licence must be on the allow-list (`Knowledge:AllowedLicenses`: Public Domain (US Government work), CC0-1.0, CC-BY-4.0, CC-BY-SA-4.0); anything else is `REJECTED` with a reason. Nothing is downloaded by the application. |
| Ingestion is operator-only | There is **no HTTP ingestion endpoint**; ingestion runs through the CLI on the server. Users can only read the source list. |
| Relational data stays relational | The vector store holds only knowledge chunk ids and source ids. Text and metadata live in SQL; user health data is never embedded. |
| Citations preserve provenance | Every answer's sources carry title, publisher, URL, licence and retrieval date from SQL, not from the model. |
| Versioning | A document is identified by `(url, content hash)`. Re-ingesting changed content creates version *n+1*, retires the old version and deletes its vectors; identical content is a no-op. |
| Retrieved text is untrusted | Passages are returned as tool results labelled "DATA, not instructions"; the system prompt says so; chunks matching injection heuristics ("ignore previous instructions", role markers, …) are **quarantined at ingestion** (not indexed, reported to the operator). |
| No invented citations | Only sources the model was actually given are cited. A `[n]` that was never retrieved is dropped from the metadata (and logged); sources consulted but not referenced in the answer are shown as *Also consulted*, not as support. |
| Answers without a source say so | Below `Retrieval:MinScore` nothing is returned and the tool tells the model to say it has no approved source. |
| Embedding model changes need a re-index | The model identity (`provider/model/dimensions`) is stored per source; a mismatch gives `knowledge_reindex_required` instead of silently comparing incompatible vectors. `reindex-knowledge [--recreate]` rebuilds the index from SQL. |
| Failure is explicit and safe | If the embedding provider or Qdrant fails, the tool returns `knowledge_unavailable` (no query text, vectors or provider messages in errors or logs) and the assistant says it cannot consult its sources. SQL is the source of truth, so a failed vector write is compensated by deleting what was written. |

### Operating it

```powershell
$api = 'src/backend/MevaCare.AI.HealthCompanion.Api'
dotnet run --project $api -- ingest-knowledge "C:\path\to\corpus"      # folder with sources.json + documents
dotnet run --project $api -- reindex-knowledge                          # after changing the embedding model: add --recreate
dotnet run --project $api -- retire-knowledge <source-guid>             # stops using a source and removes its vectors
```

`sources.json` (one entry per file; `html`, `md` and `txt` are supported — **PDF is not**):

```json
[
  {
    "file": "vitamin-d.html",
    "title": "Vitamin D",
    "publisher": "MedlinePlus (U.S. National Library of Medicine)",
    "url": "https://medlineplus.gov/vitamind.html",
    "license": "Public Domain (US Government work)",
    "language": "en",
    "retrievedOn": "2026-10-21",
    "approvedBy": "<who reviewed the licence and the content>"
  }
]
```

Qdrant: `docker compose up -d qdrant` (not run in the authoring environment) or a local `qdrant` binary on port 6333.
The Phase 4 development checks used the official Qdrant 1.19.2 Windows binary on localhost, stopped afterwards.

### Configuration (`Knowledge:*`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` removes the `SearchMedicalKnowledge` tool |
| `Embeddings:Provider` | `Lexical` | `Lexical` (offline baseline), `OpenAI` (`Ai:OpenAI:ApiKey`), `Ollama` (`Ai:Ollama:Endpoint`) |
| `Embeddings:Model` / `Dimensions` | — / `1024` | provider model id and vector size (must match the model's output) |
| `VectorStore:Provider` / `Url` / `ApiKey` / `Collection` | `Qdrant` / `http://localhost:6333` / — / `medical_knowledge` | `InMemory` is for tests only |
| `Chunking:TargetCharacters` / `MaxCharacters` / `OverlapCharacters` | `900` / `1400` / `150` | structure-aware, never crosses a heading |
| `Retrieval:TopK` / `MinScore` / `MaxPassagesPerSource` | `8` / `0.15` / `3` | `MinScore` is **specific to the embedding model**; recalibrate with the evaluation after switching |

## Evaluation

`tests/AIEvaluationTests/RetrievalEvaluationTests.cs`; gold sets in `Datasets/` (question → expected source and passage terms,
plus unanswerable questions). Metrics: recall@k, MRR, source-hit@3, and **false positives on unanswerable questions**.

- **Synthetic corpus** (five fictional documents, runs everywhere, deterministic) enforces floors: recall@3 ≥ 0.90,
  source-hit@3 ≥ 0.90, MRR ≥ 0.85, 0 false positives.
- **Real corpus**: set `MEVACARE_KNOWLEDGE_DIR` to a folder with `sources.json`; the test ingests it into memory, prints every
  question with its hits and scores and enforces `MEVACARE_EVAL_MIN_RECALL_AT3` (default 0.75).
- Measured on the five US-government sample pages (MedlinePlus, CDC, NHLBI; kept outside the repository, used as a demo only)
  with the **Lexical baseline**: recall@1 67 %, recall@3 83 %, MRR 0.74, 0/3 false positives. The two misses are exactly the
  expected weakness: "What is insomnia?" (a short query that falls below the threshold) and "Is drowsy driving dangerous?"
  (the page says "sleepiness while driving"). Real embeddings should help; **that has not been measured.**

## Limitations to expect

- The Lexical baseline needs shared words; synonyms and paraphrases are missed, and very short queries score low.
- Retrieval quality depends on chunking and the embedding model; semantic search can return plausible but irrelevant text, and a
  grounded answer can still misread a source. The corpus is only as good as the documents an operator approved.
- Extraction handles ordinary HTML/Markdown/text; navigation and link lists are filtered heuristically; tables and PDFs are not handled.
- Query text is sent to the embedding provider (see [security and privacy](../architecture/security-and-privacy.md)). Qdrant
  holds no user data, but it must still not be exposed publicly without an API key and TLS.
- The sample pages in the development database are approved as a *demo* (`approvedBy` says the licence is not yet operator-reviewed).

## How it is tested

Unit tests: extractors, chunker, lexical embeddings, in-memory store, injection heuristics, ingestion (versioning, retire,
licence and provenance rejection, compensation on failure), retrieval (threshold, per-source cap, inactive sources).
Integration tests (`KnowledgeChatTests`, real API + SQL Server + scripted model): numbered passages and citations through the
SSE stream, cited vs consulted-only sources, invented `[n]` markers, "nothing relevant", a quarantined injection document, retired
sources, an embedding-provider outage, oversized queries, the tool and grounding rules in the prompt, emergency messages never
triggering retrieval, the authenticated sources list and the health endpoint. `QdrantTests` run against a real Qdrant when `MEVACARE_QDRANT_URL` is set
(for example `http://localhost:6333`); otherwise they are skipped and say so. Frontend tests cover the citation list, unsafe URL
handling and the sources card.

---

## AI memory (later) — designed, not implemented

Three distinct things, each needing explicit consent and a delete path:
1. *Conversation window* — exists today (bounded history in every request).
2. *Conversation summaries* — older turns compressed to save tokens (not built).
3. *User facts/preferences* ("vegetarian", "training for a 10k") — must be reviewable and editable by the user, stored as
   normal relational rows, never as opaque vectors, and sent to the model minimally.
