<#
.SYNOPSIS
  Turns on "Sign in with Google" for the API by storing the Google OAuth client ID in dotnet user-secrets (outside the repository).

.DESCRIPTION
  Only the client ID is needed. It is public by design (every browser that opens the login page receives it), so there is no secret
  to protect and nothing to prompt for. The script prints the stored setting NAME and the redirect/origin values to register at Google.
  Run it in the Terminal panel (that is where the API's real user-secrets live on this machine). Restart the API afterwards.
  Create the client first, see docs/integrations/google-sign-in.md (Google Cloud Console -> Credentials -> OAuth client ID -> Web application).

.EXAMPLE
  ./scripts/setup-google-signin.ps1 -ClientId '1234567890-abc.apps.googleusercontent.com'
.EXAMPLE
  # switch the feature off again
  ./scripts/setup-google-signin.ps1 -Remove
#>
param(
  [string]$ClientId = '',
  [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\backend\MevaCare.AI.HealthCompanion.Api'
if (-not (Test-Path $project)) { throw "API project not found at $project" }

if ($Remove) {
  dotnet user-secrets remove 'Auth:Google:ClientId' --project $project | Out-Null
  Write-Host 'Removed Auth:Google:ClientId. Restart the API: the Google button disappears.'
  return
}

$ClientId = $ClientId.Trim()
if ($ClientId -notmatch '^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$') {
  throw "That does not look like a Google OAuth client ID (expected something like 1234567890-abc123.apps.googleusercontent.com). Do not paste the client secret here: it is not needed."
}

dotnet user-secrets set 'Auth:Google:ClientId' $ClientId --project $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not store Auth:Google:ClientId' }

Write-Host 'Stored Auth:Google:ClientId. Restart the API so it picks the setting up.'
Write-Host ''
Write-Host 'In Google Cloud Console (the OAuth client of type "Web application") register as "Authorized JavaScript origins":'
Write-Host '  http://localhost:5173        (the Vite dev server used in development)'
Write-Host '  https://<your production domain>   (when you deploy)'
Write-Host 'No "Authorized redirect URIs" are needed: the browser receives the ID token directly.'
