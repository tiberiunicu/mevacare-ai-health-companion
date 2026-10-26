<#
.SYNOPSIS
  Runs the live AI evaluations (real model, costs money, non-deterministic) with the provider settings stored by scripts/setup-ai.ps1.

.DESCRIPTION
  Copies the Ai:* user-secrets of the API into environment variables of THIS process only (Ai:OpenAI:Model -> Ai__OpenAI__Model), turns on
  MEVACARE_EVAL_LIVE and runs the live tests with their answers printed so that a human can read them (see docs/ai/ai-evaluation.md).
  Nothing is written to disk or to the repository; the variables vanish with the shell.

.EXAMPLE
  ./scripts/run-live-eval.ps1
.EXAMPLE
  ./scripts/run-live-eval.ps1 -Filter 'FullyQualifiedName~MealExtraction'
#>
param(
  [string]$Filter = 'FullyQualifiedName~LiveModelEvaluationTests|FullyQualifiedName~MealExtractionEvaluationTests'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$project = Join-Path $root 'src\backend\MevaCare.AI.HealthCompanion.Api'

$names = @()
dotnet user-secrets list --project $project | Where-Object { $_ -like 'Ai:*' } | ForEach-Object {
  $name, $value = $_ -split ' = ', 2
  $variable = $name -replace ':', '__'
  [Environment]::SetEnvironmentVariable($variable, $value, 'Process')
  $names += $variable
}
if ($names.Count -eq 0) { throw 'No Ai:* user-secrets found. Run scripts/setup-ai.ps1 first.' }
Write-Host "Using $($names.Count) Ai settings from the API's user-secrets."

# The variables are removed afterwards so that they do not leak into later commands of the same shell (the integration tests would read them).
try {
  $env:MEVACARE_EVAL_LIVE = '1'
  dotnet test (Join-Path $root 'tests\AIEvaluationTests') --filter $Filter --logger 'console;verbosity=detailed'
}
finally {
  $names + 'MEVACARE_EVAL_LIVE' | ForEach-Object { [Environment]::SetEnvironmentVariable($_, $null, 'Process') }
}
