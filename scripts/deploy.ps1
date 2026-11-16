<#
.SYNOPSIS
  Deploys the COMMITTED code of this repository to the server and (re)builds and restarts the stack.

.DESCRIPTION
  Only what is committed is deployed (git archive of HEAD), never uncommitted local changes or local data. The images are built on the
  server (there is no Docker on the development machine), the database is migrated by the API on start, and the other containers are
  not touched when their image did not change. Secrets are not part of this: see scripts/deploy-secrets.ps1.

.EXAMPLE
  ./scripts/deploy.ps1
.EXAMPLE
  # only show the status of the stack
  ./scripts/deploy.ps1 -StatusOnly
#>
param(
  [string]$Server = 'root@89.44.124.202',
  [string]$Key = (Join-Path $HOME '.ssh\mevacare_vps'),
  [switch]$StatusOnly
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Key)) { throw "SSH key not found at $Key" }
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

function Invoke-Remote([string]$command) {
  & ssh -o BatchMode=yes -o ConnectTimeout=20 -i $Key $Server $command
  if ($LASTEXITCODE -ne 0) { throw "Remote command failed (exit $LASTEXITCODE)." }
}

$compose = 'docker compose -p mevacare-hc --env-file .env -f src/deploy/docker-compose.yml'

if ($StatusOnly) {
  Invoke-Remote "cd ~/mevacare-hc && $compose ps && free -m | head -2"
  return
}

Push-Location $repo
try {
  if (git status --porcelain) { Write-Warning 'There are uncommitted changes: they are NOT deployed (only the last commit is).' }
  $commit = (git rev-parse --short HEAD)
  $archive = Join-Path ([IO.Path]::GetTempPath()) "mevacare-hc-$commit.tar.gz"
  git archive --format=tar.gz -o $archive HEAD
  if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
} finally { Pop-Location }

Write-Host "Uploading commit $commit ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)..."
& scp -o BatchMode=yes -i $Key $archive "${Server}:/tmp/mevacare-hc-src.tar.gz"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
Remove-Item $archive

Invoke-Remote 'test -f ~/mevacare-hc/.env && test -f ~/mevacare-hc/api.generated.env && test -f ~/mevacare-hc/api.copied.env || { echo "Run scripts/deploy-secrets.ps1 first."; exit 1; }'
Invoke-Remote "cd ~/mevacare-hc && rm -rf src && mkdir src && tar -xzf /tmp/mevacare-hc-src.tar.gz -C src && rm /tmp/mevacare-hc-src.tar.gz && echo $commit > src/DEPLOYED_COMMIT"
Invoke-Remote "docker network inspect hc_edge >/dev/null 2>&1 || docker network create hc_edge"
Write-Host 'Building and starting (the first build takes several minutes)...'
Invoke-Remote "cd ~/mevacare-hc && $compose up -d --build"
Invoke-Remote "cd ~/mevacare-hc && $compose ps"
Write-Host "Deployed commit $commit."
