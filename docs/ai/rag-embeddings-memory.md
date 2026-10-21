# Embeddings, vector databases, RAG, citations and AI memory

> **Status: designed, not implemented.** Planned for Phase 4 (RAG) and later (memory). No Qdrant code, embedding code or
> medical documents exist yet; `docker-compose.yml` only provisions a Qdrant container for that phase.

## What problem it solves

The model's built-in knowledge is unverifiable and may be outdated. Retrieval-augmented generation (RAG) lets the assistant
answer medical-knowledge questions (for example "what is HRV?") from **approved documents** and cite them.
It is *not* used for the user's own numbers: totals, trends and measurements come from SQL, which is exact.

## Concepts, connected to the planned code

| Concept | Meaning | Planned abstraction (Modules.Knowledge) |
|---|---|---|
| Embedding | a vector capturing the meaning of a text; similar meaning → close vectors | `IEmbeddingService` over `IEmbeddingGenerator<string, Embedding<float>>` |
| Vector database | stores vectors with metadata and finds nearest neighbours | `IVectorStore`, first implementation Qdrant |
| Ingestion | extract text → chunk → embed → store with metadata | `IDocumentIngestionService` (admin-only, from an allow-list of approved documents) |
| Retrieval | embed the question, fetch top-k chunks, optionally filter by metadata | `IKnowledgeRetrievalService` |
| Context assembly | put the chunks in the prompt, labelled as *untrusted reference text* with source ids | `SearchMedicalKnowledge` tool → grounded answer with citations |
| Memory | what the assistant remembers across conversations | see below |

## Planned rules

- **Relational data stays relational.** Embeddings never replace SQL; the vector store holds only knowledge chunks.
- **Only approved content.** No automatic downloading of medical text from the internet. A document enters the system only
  with an identifiable source, licence and version recorded as metadata (`SourceTitle`, `Publisher`, `Url`, `License`,
  `RetrievedOn`, section).
- **Citations are mandatory** for answers that use retrieved chunks; the answer lists the sources of the chunks it was given,
  and answers with no sufficiently similar chunk say so instead of guessing (similarity threshold, evaluated).
- **Retrieved text is untrusted input** (indirect prompt injection): it is delimited, never placed in the system role, and
  the model has no write tools in the same turn that processes documents.
- Embedding model, dimension and distance metric are configuration and recorded with the collection; changing the model
  means re-embedding.
- Chunking: structure-aware (headings/paragraphs), a few hundred tokens with small overlap, chunk metadata keeps the parent
  document and position so citations can point to it.

## AI memory (later)

Three distinct things, each needing explicit consent and a delete path:
1. *Conversation window* — exists today (bounded history in every request).
2. *Conversation summaries* — older turns compressed to save tokens (not built).
3. *User facts/preferences* ("vegetarian", "training for a 10k") — must be reviewable and editable by the user, stored as
   normal relational rows, never as opaque vectors, and sent to the model minimally.

## Limitations to expect

Retrieval quality depends on chunking and the embedding model; semantic search can return plausible but irrelevant
chunks; grounded answers can still misread a source. The corpus is only as good as the documents approved for it.

## How it will be tested

A small gold dataset (question → expected source/chunk) to measure recall@k and citation correctness in
`tests/AIEvaluationTests`, ingestion tests with fixture documents, vector-store tests against a Qdrant container, and
injection tests where a document contains instructions.
