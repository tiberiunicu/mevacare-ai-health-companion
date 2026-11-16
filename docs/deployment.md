# Deployment (demo on the existing VPS)

Status: **run on 2026-11-16 and working**: `https://health-companion.mevacare.com` is live over HTTPS (Let's Encrypt), the images built on the first try, the migration created the database, the five knowledge documents are indexed with the real embedding model (`/health/knowledge` reports 5 active sources), and the Google button appears. There is no Docker on the development machine, so everything was built on the server. What the first run found and fixed: the key-ring folder was not writable by the non-root API user (commit `1a4a519`). Data loaded: 5 knowledge documents (21 chunks), USDA SR Legacy (7,793 generic foods, the figures in the nutrition docs) and the Romanian Open Food Facts products (4,028 accepted of 5,278 candidates; the import took about 20 seconds). Not done yet: a real sign-in and chat on the live site, a restart test, the other Open Food Facts countries.

Target: `https://health-companion.mevacare.com`, on the Romarg VPS (Ubuntu 24.04, 2 vCPU, **3.9 GB RAM, no swap**, 40 GB free) that already runs the v1 MevaCare stack (`~/mevacare_v1_2026`: nginx `web` on ports 80/443, `api`, MySQL). Both apps are used only for demos.

## Architecture

```
Internet ──443──►  v1 `web` (nginx, certificates for both names)         ← existing container, gets one extra conf file
                      │  health-companion.mevacare.com
                      ▼   shared Docker network `hc_edge`
                   hc-web (nginx: single-page app, /api proxy, SSE unbuffered)
                      ▼
                   api (.NET 10) ──► sqlserver (SQL Server 2022 Express)   not published
                                 └─► qdrant (v1.20.0)                      not published
                                 └─► Azure AI (chat + embeddings) over HTTPS
```

- The VPS has one public address and v1 owns ports 80/443, so the new name is served by the **same nginx**: one extra file (`deploy/vps/health-companion.edge.conf`) is mounted into v1's `web` container through a compose *override* file. v1's own files are not edited; deleting the override file undoes everything.
- The API trusts `X-Forwarded-For/Proto` only from private networks (`ForwardedHeaders:TrustedNetworks`), so rate limits count the visitor, not the proxy (integration-tested).
- Memory budget (approximate): v1 stack 0.7 GB, SQL Server limited to 1.4 GB, API ≤ 0.9 GB, Qdrant ≤ 0.5 GB, nginx small. It fits, with little slack: hence the swap file, and the limits in `deploy/docker-compose.yml`.
- Secrets never enter the repository: three files in `~/mevacare-hc` on the server (`.env`, `api.generated.env`, `api.copied.env`, mode 600), written by `scripts/deploy-secrets.ps1`, which generates the JWT key and SQL password itself and copies the AI/WHOOP/Google settings from your local user-secrets without printing them.

## Steps

**0. Before anything (you).**
1. DNS: in the Romarg cPanel add an `A` record `health-companion` → `89.44.124.202`. Check with `nslookup health-companion.mevacare.com 8.8.8.8`.
2. Google Cloud Console: `https://health-companion.mevacare.com` is already in the OAuth client's authorized JavaScript origins (done).
3. WHOOP (optional, later): add `https://health-companion.mevacare.com/api/integrations/whoop/callback` as a redirect URL in the WHOOP developer dashboard.

**1. Swap (once, on the VPS).** Run `deploy/vps/add-swap.sh` on the server (from PowerShell: pipe the file into `ssh ... "tr -d '' | sh -s"`; `Get-Content -Raw` may add a harmless BOM that makes the first line fail).

**2. Secrets (once, in the Terminal panel).** `./scripts/deploy-secrets.ps1`

**3. Build and start the stack.** `./scripts/deploy.ps1` (uploads the *committed* code, builds on the server, first time 8 to 15 minutes). The stack runs, but is not reachable from outside yet. Check: `./scripts/deploy.ps1 -StatusOnly`.

**4. Certificate for the new name (v1 is down for about 30 seconds).** On the VPS, in `~/mevacare_v1_2026`:
```sh
docker compose stop web
docker compose run --rm --service-ports certbot certonly --standalone -d health-companion.mevacare.com --dry-run   # a rehearsal: issues nothing
docker compose run --rm --service-ports certbot certonly --standalone -d health-companion.mevacare.com
docker compose up -d web
```

**5. Switch the new name on (v1 restarts for a few seconds).** On the VPS:
```sh
cd ~/mevacare_v1_2026
mkdir -p edge
cp ~/mevacare-hc/src/deploy/vps/health-companion.edge.conf edge/health-companion.conf
cp ~/mevacare-hc/src/deploy/vps/v1-docker-compose.override.yml docker-compose.override.yml
docker compose up -d web
```
If v1 does not come back: `rm docker-compose.override.yml && docker compose up -d web` restores it exactly as it was.

**6. Renewals.** Replace v1's cron entry (it only watches the old certificate) with `deploy/vps/renew-certs.sh`:
```sh
cp ~/mevacare-hc/src/deploy/vps/renew-certs.sh ~/mevacare_v1_2026/renew-certs.sh && chmod +x ~/mevacare_v1_2026/renew-certs.sh
( crontab -l | sed 's#renew-cert.sh#renew-certs.sh#' ) | crontab -
```

**7. Data (demo content).**
- Medical knowledge: copy the five documents (`.local/knowledge-corpus`) to `~/mevacare-hc/data/corpus` and run
  `docker compose -p mevacare-hc --env-file .env -f src/deploy/docker-compose.yml run --rm api ingest-knowledge /data/corpus`
  (needs the embedding settings copied in step 2; embeddings are made by the real model).
- Foods: copy the Open Food Facts dump (`.local/off/*.csv.gz`, 0.8 GB) to `~/mevacare-hc/data/off` and run
  `... run --rm api import-openfoodfacts /data/off/en.openfoodfacts.org.products.csv.gz romania` (add countries as wanted; all eight countries are about 1.05 million products and a lot for this server, Romania alone is 4,028). The generic USDA foods matter most for sentences such as "two eggs and toast": download "SR Legacy" (CSV zip, ~6 MB) from fdc.nal.usda.gov, copy `food.csv food_nutrient.csv nutrient.csv food_portion.csv measure_unit.csv food_category.csv sr_legacy_food.csv` to `~/mevacare-hc/data/usda` and run `... run --rm api import-usda /data/usda`. The imports are repeatable: rows already present are skipped.

**8. Check.** `https://health-companion.mevacare.com/health/ready` answers `Healthy`; the login page shows the Google button; register, sign in, log a meal in chat.

## Operating it

| Task | Command (on the VPS, in `~/mevacare-hc`, with `C="docker compose -p mevacare-hc --env-file .env -f src/deploy/docker-compose.yml"`) |
|---|---|
| Status, memory | `$C ps` · `docker stats --no-stream` |
| Logs | `$C logs -f --tail 100 api` |
| Update to the latest commit | on the PC: `./scripts/deploy.ps1` |
| Restart | `$C restart api` |
| Database backup | `$C exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$(grep MSSQL_SA_PASSWORD .env \| cut -d"'" -f2)" -Q "BACKUP DATABASE MevaCareAIHealthCompanion TO DISK='/var/opt/mssql/backup.bak' WITH INIT, COMPRESSION"`, then copy the file out with `docker cp` |
| Stop everything of this app | `$C down` (volumes, hence data, are kept; `down -v` deletes them) |
| Remove the new name from the edge | `rm ~/mevacare_v1_2026/docker-compose.override.yml && cd ~/mevacare_v1_2026 && docker compose up -d web` |

## Not covered / honest limits

- **Never built or run.** See the status line. Expect a first round of fixes.
- **SQL Server runs as `sa`** (inside the private network, not published) to let the migration create the database. A dedicated login with fewer rights is a hardening step.
- **Data Protection keys are stored unencrypted** in the `api-keys` volume (ASP.NET logs "No XML encryptor configured"): anyone who can read the Docker volumes on the server can decrypt the stored WHOOP tokens. Acceptable for a demo; use a certificate or a key vault for real use.
- **PowerShell 5.1 and `deploy.ps1`:** do not redirect its output with `2>&1` — Docker writes build progress to stderr and PowerShell then aborts the script.
- **No automatic backups**, no monitoring or alerts, no log shipping, no staging environment; one server, one instance (the hourly WHOOP sync runs inside the API).
- **No Content-Security-Policy yet**; Google sign-in needs `accounts.google.com/gsi/` allowed when one is added ([integrations/google-sign-in.md](integrations/google-sign-in.md)).
- Demo data only: the legal texts, DPIA and processor agreements listed in [remaining-work.md](remaining-work.md) are not done, so no real person's health data belongs on this server.
- The v1 stack on the same machine shares its CPU, memory and nginx: a mistake in step 5 can take v1 down until the override file is removed.
