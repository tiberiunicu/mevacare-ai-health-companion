<#
.SYNOPSIS
  Puts the application's secrets on the server, without showing them: nothing is printed except setting NAMES.

.DESCRIPTION
  Creates (or refreshes) three files in ~/mevacare-hc on the server, all readable by root only:
    .env                 SQL Server administrator password (generated here, random) and the public origin
    api.generated.env    JWT signing key and the database connection string (generated here, written ONCE: if the files already exist
                         they are kept, because the SQL password only takes effect when the database is first created)
    api.copied.env       the AI-provider, embedding, WHOOP and Google settings COPIED from your local user-secrets
                         (rewritten on every run, so run it again after changing them locally)

  Run it in the Terminal panel: that is where the API's real user-secrets live on this machine (they are not visible to other shells).
  Fixed production settings (database migration on start, the Qdrant address, the redirect URL, ...) are in deploy/docker-compose.yml
  and win over anything copied, so a local value such as http://localhost can never reach production.

.EXAMPLE
  ./scripts/deploy-secrets.ps1
#>
param(
  [string]$Server = 'root@89.44.124.202',
  [string]$Key = (Join-Path $HOME '.ssh\mevacare_vps'),
  [string]$PublicOrigin = 'https://health-companion.mevacare.com'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\backend\MevaCare.AI.HealthCompanion.Api'
if (-not (Test-Path $project)) { throw "API project not found at $project" }
if (-not (Test-Path $Key)) { throw "SSH key not found at $Key" }

function Invoke-Remote([string]$command) {
  $output = & ssh -o BatchMode=yes -o ConnectTimeout=20 -i $Key $Server $command
  if ($LASTEXITCODE -ne 0) { throw "Remote command failed (exit $LASTEXITCODE)." }
  return $output
}

function New-Token([int]$bytes) {
  $buffer = New-Object byte[] $bytes
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
  return [Convert]::ToBase64String($buffer)
}

function Send-File([string]$remoteName, [string]$content) {
  $encoded = [Convert]::ToBase64String([System.Text.UTF8Encoding]::new($false).GetBytes($content))
  Invoke-Remote "umask 077; mkdir -p ~/mevacare-hc && echo $encoded | base64 -d > ~/mevacare-hc/$remoteName && chmod 600 ~/mevacare-hc/$remoteName" | Out-Null
}

function Quote-Env([string]$name, [string]$value) {
  if ($value -match "['\r\n]") { throw "The value of $name contains a quote or a line break; put it on the server by hand." }
  return "$name='$value'"
}

# 1. Generated once ---------------------------------------------------------------------------------------------------------------
$exists = (Invoke-Remote 'test -f ~/mevacare-hc/.env && test -f ~/mevacare-hc/api.generated.env && echo yes || echo no') -join ''
if ($exists -eq 'yes') {
  Write-Host 'Generated secrets already exist on the server: kept as they are.'
} else {
  $alphabet = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
  $random = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
  $saPassword = (($random | ForEach-Object { $alphabet[$_ % $alphabet.Length] }) -join '') + 'aA1'
  $jwtKey = New-Token 48
  $connection = "Server=sqlserver,1433;Database=MevaCareAIHealthCompanion;User Id=sa;Password=$saPassword;TrustServerCertificate=True"

  Send-File '.env' ((@(
        (Quote-Env 'MSSQL_SA_PASSWORD' $saPassword),
        (Quote-Env 'PUBLIC_ORIGIN' $PublicOrigin)
      ) -join "`n") + "`n")
  Send-File 'api.generated.env' ((@(
        (Quote-Env 'Jwt__SigningKey' $jwtKey),
        (Quote-Env 'ConnectionStrings__Default' $connection)
      ) -join "`n") + "`n")
  Write-Host 'Generated and stored: MSSQL_SA_PASSWORD, PUBLIC_ORIGIN, Jwt__SigningKey, ConnectionStrings__Default (values not shown).'
}

# 2. Copied from the local user-secrets ---------------------------------------------------------------------------------------------
$wanted = @('Ai:', 'Knowledge:Embeddings:', 'Knowledge:Retrieval:', 'Integrations:Whoop:ClientId', 'Integrations:Whoop:ClientSecret', 'Integrations:Whoop:ClientAuthentication', 'Auth:Google:ClientId', 'Email:')
$lines = New-Object System.Collections.Generic.List[string]
$names = New-Object System.Collections.Generic.List[string]
foreach ($entry in (& dotnet user-secrets list --project $project)) {
  $split = $entry.IndexOf(' = ')
  if ($split -lt 1) { continue }
  $name = $entry.Substring(0, $split)
  $value = $entry.Substring($split + 3)
  if (-not ($wanted | Where-Object { $name.StartsWith($_, [StringComparison]::Ordinal) })) { continue }
  $lines.Add((Quote-Env ($name.Replace(':', '__')) $value))
  $names.Add($name)
}
if ($lines.Count -eq 0) { throw 'No AI/Google/WHOOP settings found in the local user-secrets. Run this script in the Terminal panel.' }
Send-File 'api.copied.env' (($lines -join "`n") + "`n")

Write-Host "Copied $($names.Count) settings from your user-secrets to the server (names only):"
$names | Sort-Object | ForEach-Object { "  $_" }
