<#
.SYNOPSIS
  Stores the WHOOP developer-app credentials of the API in dotnet user-secrets (outside the repository).

.DESCRIPTION
  The client secret is read from a hidden prompt: it never appears on the command line, in the shell history or in this repository.
  The script prints only the NAMES of the stored settings. Re-run any time; existing values are overwritten.
  Run it in the Terminal panel (that is where the API's real user-secrets live on this machine). Restart the API afterwards.

.EXAMPLE
  ./scripts/setup-whoop.ps1 -ClientId '<client id from the WHOOP developer dashboard>'
.EXAMPLE
  # only when the redirect URL registered at WHOOP differs from the default (for example an HTTPS tunnel)
  ./scripts/setup-whoop.ps1 -ClientId '<client id>' -RedirectUri 'https://<tunnel>/api/integrations/whoop/callback' -FrontendBaseUrl 'https://<tunnel>'
.EXAMPLE
  # if WHOOP rejects credentials in the form body of the token request
  ./scripts/setup-whoop.ps1 -ClientId '<client id>' -ClientAuthentication Basic
#>
param(
  [Parameter(Mandatory)][string]$ClientId,
  [string]$RedirectUri = '',
  [string]$FrontendBaseUrl = '',
  [ValidateSet('', 'Post', 'Basic')][string]$ClientAuthentication = ''
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\backend\MevaCare.AI.HealthCompanion.Api'
if (-not (Test-Path $project)) { throw "API project not found at $project" }

$secureSecret = Read-Host 'WHOOP client secret (input is hidden)' -AsSecureString
$secret = [System.Net.NetworkCredential]::new('', $secureSecret).Password
if ([string]::IsNullOrWhiteSpace($secret)) { throw 'No secret entered.' }

function Set-Secret([string]$name, [string]$value) {
  dotnet user-secrets set $name $value --project $project | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Could not store $name" }
}

Set-Secret 'Integrations:Whoop:ClientId' $ClientId.Trim()
Set-Secret 'Integrations:Whoop:ClientSecret' $secret
if ($RedirectUri) { Set-Secret 'Integrations:Whoop:RedirectUri' $RedirectUri.Trim() }
if ($FrontendBaseUrl) { Set-Secret 'Integrations:FrontendBaseUrl' $FrontendBaseUrl.Trim() }
if ($ClientAuthentication) { Set-Secret 'Integrations:Whoop:ClientAuthentication' $ClientAuthentication }

Write-Host 'Stored WHOOP settings (names only):'
dotnet user-secrets list --project $project | Where-Object { $_ -like 'Integrations:*' } | ForEach-Object { '  ' + ($_ -split ' = ')[0] }
Write-Host 'Restart the API so it picks the settings up.'
