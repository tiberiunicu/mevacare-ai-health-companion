<#
.SYNOPSIS
  Runs the live AI evaluations (real model, costs money, non-deterministic) with the provider settings stored by scripts/setup-ai.ps1.

.DESCRIPTION
  Copies the Ai:* and Knowledge:* user-secrets of the API into environment variables of THIS process only (Ai:OpenAI:Model -> Ai__OpenAI__Model),
  turns on MEVACARE_EVAL_LIVE and runs the tests with their output printed so that a human can read it (see docs/ai/ai-evaluation.md).
  Nothing is written to disk or to the repository; the variables are removed again when the script ends.

.EXAMPLE
  ./scripts/run-live-eval.ps1
.EXAMPLE
  ./scripts/run-live-eval.ps1 -Filter 'FullyQualifiedName~MealExtraction'
.EXAMPLE
  # retrieval with the real embedding model, on the five sample pages in .local/knowledge-corpus, trying another MinScore
  ./scripts/run-live-eval.ps1 -Filter 'FullyQualifiedName~RetrievalEvaluationTests' -KnowledgeDir .local/knowledge-corpus -MinScore 0.3
#>
param(
  [string]$Filter = 'FullyQualifiedName~LiveModelEvaluationTests|FullyQualifiedName~MealExtractionEvaluationTests',
  # Folder with sources.json for the real-corpus retrieval evaluation (MEVACARE_KNOWLEDGE_DIR).
  [string]$KnowledgeDir = '',
  # Overrides Knowledge:Retrieval:MinScore for this run only.
  [string]$MinScore = ''
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$project = Join-Path $root 'src\backend\MevaCare.AI.HealthCompanion.Api'

$names = @()
dotnet user-secrets list --project $project | Where-Object { $_ -like 'Ai:*' -or $_ -like 'Knowledge:*' } | ForEach-Object {
  $name, $value = $_ -split ' = ', 2
  $variable = $name -replace ':', '__'
  [Environment]::SetEnvironmentVariable($variable, $value, 'Process')
  $names += $variable
}
if (-not ($names | Where-Object { $_ -like 'Ai__*' })) { throw 'No Ai:* user-secrets found. Run scripts/setup-ai.ps1 first.' }
Write-Host "Using $($names.Count) settings from the API's user-secrets."

# The variables are removed afterwards so that they do not leak into later commands of the same shell (the integration tests would read them).
try {
  $names += 'MEVACARE_EVAL_LIVE'
  $env:MEVACARE_EVAL_LIVE = '1'
  if ($KnowledgeDir) {
    $names += 'MEVACARE_KNOWLEDGE_DIR'
    $env:MEVACARE_KNOWLEDGE_DIR = (Resolve-Path (Join-Path $root $KnowledgeDir)).Path
  }
  if ($MinScore) {
    $names += 'Knowledge__Retrieval__MinScore'
    $env:Knowledge__Retrieval__MinScore = $MinScore
  }
  dotnet test (Join-Path $root 'tests\AIEvaluationTests') --filter $Filter --logger 'console;verbosity=detailed'
}
finally {
  $names | ForEach-Object { [Environment]::SetEnvironmentVariable($_, $null, 'Process') }
}
