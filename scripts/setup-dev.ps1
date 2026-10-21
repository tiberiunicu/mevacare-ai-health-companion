<#
.SYNOPSIS
  Stores the local development secrets of the API in dotnet user-secrets (outside the repository).

.DESCRIPTION
  - Sets ConnectionStrings:Default for the SQL Server you choose.
  - Generates a random Jwt:SigningKey if none exists yet.
  Nothing is written to the repository. Re-run any time; an existing signing key is kept.

.EXAMPLE
  ./scripts/setup-dev.ps1 -SqlServer 'localhost\SQLEXPRESS'
.EXAMPLE
  ./scripts/setup-dev.ps1 -SqlServer 'localhost,14330' -SqlUser sa -SqlPassword (Read-Host -AsSecureString)
#>
param(
  [string]$SqlServer = 'localhost',
  [string]$Database = 'MevaCareAIHealthCompanion',
  [string]$SqlUser,
  [securestring]$SqlPassword
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\backend\MevaCare.AI.HealthCompanion.Api'

if ($SqlUser) {
  if (-not $SqlPassword) { throw 'Provide -SqlPassword together with -SqlUser.' }
  $plain = [System.Net.NetworkCredential]::new('', $SqlPassword).Password
  $connection = "Server=$SqlServer;Database=$Database;User Id=$SqlUser;Password=$plain;TrustServerCertificate=true"
} else {
  $connection = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true"
}

dotnet user-secrets set 'ConnectionStrings:Default' $connection --project $project | Out-Null
Write-Host "Connection string stored for server '$SqlServer', database '$Database'."

$existing = dotnet user-secrets list --project $project | Where-Object { $_ -like 'Jwt:SigningKey*' }
if (-not $existing) {
  $bytes = New-Object byte[] 48
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  $rng.GetBytes($bytes)
  $rng.Dispose()
  dotnet user-secrets set 'Jwt:SigningKey' ([Convert]::ToBase64String($bytes)) --project $project | Out-Null
  Write-Host 'Generated a new Jwt:SigningKey.'
} else {
  Write-Host 'Jwt:SigningKey already present; kept.'
}

Write-Host "`nNext: dotnet run --project src/backend/MevaCare.AI.HealthCompanion.Api --launch-profile http"
