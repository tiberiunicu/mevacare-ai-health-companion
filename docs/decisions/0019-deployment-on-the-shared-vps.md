# ADR-0019: Demo deployment on the existing VPS, behind the v1 nginx

**Status:** Accepted; the files are written and the application behaviour it depends on is tested, but nothing has been built or started on the server yet ([deployment.md](../deployment.md)).

**Context.** The owner wants a demo of the application at `health-companion.mevacare.com` on the Romarg VPS that already runs the v1 MevaCare stack (nginx on ports 80/443, certificates in a Docker volume, MySQL). The VPS has 3.9 GB of RAM and no swap. This application needs SQL Server, Qdrant, the .NET API and the static frontend. There is no Docker on the development machine and no Git remote.

**Decision.**
- **One compose stack** (`deploy/docker-compose.yml`): `web` (nginx: the single-page app and a streaming `/api` proxy), `api`, `sqlserver` (2022 **Express**: free for production, 10 GB limit, memory capped), `qdrant`. Only `web` joins a shared network; the data services are not published.
- **The v1 nginx is the edge.** Ports 80/443 are taken, so one extra `conf.d` file is mounted into v1's `web` container through a compose *override* file (v1's own files stay untouched; deleting the override undoes it). The upstream is resolved per request and the certificate is issued before the file is installed, so a problem on the new side cannot stop v1 from starting. Alternatives: moving both sites behind a new proxy (a bigger change to a live site), another port (unusable for visitors), a second VPS (cost, and the owner chose to share this one for demos).
- **The API trusts `X-Forwarded-*` only from private networks** (`ForwardedHeaders:TrustedNetworks`, whole proxy chain walked), so per-IP rate limits and HSTS work behind two proxies. This is code (`ForwardedHeadersSetup`) with integration tests.
- **Secrets stay out of the repository and out of the conversation:** `scripts/deploy-secrets.ps1` generates the JWT key and SQL password, and copies the AI, embedding, WHOOP and Google settings from the developer's user-secrets to files readable by root only; it prints names, never values. Fixed production settings live in the compose file and win over anything copied.
- **Deploys ship committed code only** (`git archive` + scp, build on the server): reproducible, and local data (`.local`, test accounts) cannot leak.
- **Certificates** come from Let's Encrypt through v1's existing certbot volume; the renewal script now looks at every certificate, because v1's script watched only the first one.

**Consequences.** Cheap and quick; shares CPU, memory and nginx with v1 (a wrong step can take v1 down until the override is removed); fits in memory only with the limits and a swap file; SQL runs as `sa` inside the private network; no backups or monitoring yet. Not suitable for real people's health data: see [remaining-work.md](../remaining-work.md).
