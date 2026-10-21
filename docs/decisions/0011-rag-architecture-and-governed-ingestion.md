# ADR-0011: RAG with SQL as the system of record, a derived vector index, and operator-governed ingestion

**Status:** Accepted

**Context.** The assistant must answer medical-knowledge questions from trusted documents and show where each claim comes from. The documents are third-party
text (licences, provenance, possible prompt injection), the user's own numbers must stay exact, and the vector database must be replaceable and rebuildable.

**Decision.**
- **SQL is the system of record.** `knowledge.Sources` (title, publisher, URL, licence, retrieval date, approver, content hash, version, status, embedding model) and `knowledge.Chunks` (text, heading, ordinal) hold everything needed for a citation.
  The vector store (Qdrant, cosine) holds only chunk id + source id + vector. It is derived data: `reindex-knowledge [--recreate]` rebuilds it from SQL, and retrieval re-loads text and provenance from SQL for **active** sources only, so a retired source disappears from answers even if a vector lingers.
- **Governed ingestion.** Only an operator can add documents, through a CLI (`ingest-knowledge`) with a manifest (`sources.json`). There is no HTTP ingestion endpoint and the application never fetches documents itself.
  Required provenance fields, an http(s) URL, a **licence allow-list**, and a content hash. Changed content becomes version *n+1* (old version retired, vectors deleted); identical content is a no-op.
- **Retrieval is a tool, not a prompt prefix.** The model calls `SearchMedicalKnowledge`, which returns numbered passages labelled as data; the answer cites `[n]`. A knowledge question therefore costs one embedding call plus a vector query, and chat turns that need no reference text cost nothing extra.
  Emergency messages never reach the model, so they never trigger retrieval.
- **Honest citations.** Sources are built from what was actually retrieved (never from model text). Sources referenced by `[n]` are *cited*; others are shown as *consulted*; unknown markers are dropped and logged. If nothing scores above `MinScore`, the model is told to say it has no approved source.
- **Untrusted text.** Passages are data (labelled in tool results and the prompt); chunks that match injection heuristics are quarantined at ingestion and reported.
- **Failure behaviour.** SQL write first, vector write second, compensating vector delete if the SQL commit fails. Embedding or Qdrant failures surface as stable `knowledge_unavailable`/`knowledge_reindex_required` codes, without query text or provider messages.
- User health data is **never** embedded or sent to the vector store.

**Alternatives.** Qdrant as the only store (loses transactional versioning, makes provenance part of an opaque payload and makes rebuilds impossible); always-on retrieval for every message (cost, latency, irrelevant context, harder to test);
user-uploaded documents (licence and injection risks, a separate trust model); SQL Server vector search (viable later, but the project brief asks for a dedicated vector database and keeps the retrieval backend swappable behind `IVectorStore`); using the web at question time (unverifiable, non-reproducible).

**Consequences.** The repository ships no medical content, so a fresh install answers reference questions with "no approved source" until an operator ingests a corpus. Two stores must be kept consistent (mitigated by rebuild + compensation).
The quality ceiling is the corpus and the embedding model, not the plumbing. PDFs, tables and non-English sources need more extraction work. The citation guarantees are about provenance and honesty of the list, not about the medical correctness of a source or of the model's reading of it.
