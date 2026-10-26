<#
.SYNOPSIS
  Stores the AI provider settings of the API in dotnet user-secrets (outside the repository).

.DESCRIPTION
  Configures the OpenAI-compatible provider, which also covers Azure OpenAI / Microsoft Foundry through its "/openai/v1/" endpoint.
  The API key is read from a hidden prompt: it never appears on the command line, in the shell history or in this repository.
  The script prints only the NAMES of the stored settings. Re-run any time; existing values are overwritten.

.EXAMPLE
  ./scripts/setup-ai.ps1 -Endpoint 'https://<resource>.services.ai.azure.com/openai/v1/' -Model '<deployment name>'
.EXAMPLE
  # a reasoning model that needs "none" for function tools and rejects temperature (verified for gpt-6-luna on Azure)
  ./scripts/setup-ai.ps1 -Endpoint 'https://<resource>.services.ai.azure.com/openai/v1/' -Model 'gpt-6-luna' -ReasoningEffort none -OmitTemperature
.EXAMPLE
  # plain OpenAI (no endpoint needed)
  ./scripts/setup-ai.ps1 -Model '<model id>'
#>
param(
  [string]$Endpoint = '',
  [Parameter(Mandatory)][string]$Model,
  [double]$Temperature = 0.3,
  [int]$MaxOutputTokens = 800,
  # Reasoning models (GPT-5 / GPT-6 families, o-series): some refuse function tools unless the effort is "none", and reject temperature.
  [ValidateSet('', 'none', 'low', 'medium', 'high')][string]$ReasoningEffort = '',
  [switch]$OmitTemperature,
  [int]$ReasoningTokenAllowance = 0
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\backend\MevaCare.AI.HealthCompanion.Api'
if (-not (Test-Path $project)) { throw "API project not found at $project" }

$secureKey = Read-Host 'API key (input is hidden)' -AsSecureString
$key = [System.Net.NetworkCredential]::new('', $secureKey).Password
if ([string]::IsNullOrWhiteSpace($key)) { throw 'No key entered.' }

function Set-Secret([string]$name, [string]$value) {
  dotnet user-secrets set $name $value --project $project | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Could not store $name" }
}

Set-Secret 'Ai:Provider' 'OpenAI'
if ($Endpoint) { Set-Secret 'Ai:OpenAI:Endpoint' $Endpoint }
Set-Secret 'Ai:OpenAI:Model' $Model
Set-Secret 'Ai:OpenAI:ApiKey' $key
Set-Secret 'Ai:Temperature' ([string]::Format([cultureinfo]::InvariantCulture, '{0}', $Temperature))
Set-Secret 'Ai:MaxOutputTokens' ([string]$MaxOutputTokens)
Set-Secret 'Ai:SendTemperature' $(if ($OmitTemperature) { 'false' } else { 'true' })
Set-Secret 'Ai:ReasoningTokenAllowance' ([string]$ReasoningTokenAllowance)
if ($ReasoningEffort) { Set-Secret 'Ai:OpenAI:ReasoningEffort' $ReasoningEffort }

Write-Host 'Stored settings (names only):'
dotnet user-secrets list --project $project | ForEach-Object { '  ' + ($_ -split ' = ')[0] }
