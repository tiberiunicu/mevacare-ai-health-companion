# Structured outputs

> **Status: implemented in Phase 3** for meal extraction (`Modules.Assistant/Tools/MealParser.cs`).

## What problem it solves

Free text from a model cannot be fed into code. For meal logging the backend needs a list of
`{ food name, amount, unit, raw/cooked }`. Structured output constrains the model to a JSON schema so the answer can be
deserialised and validated.

## How it works here

1. The `PrepareMeal` tool receives the user's description and calls `MealParser.ParseAsync`.
2. `MealParser` calls `IChatClient.GetResponseAsync<MealExtraction>()` (Microsoft.Extensions.AI). The library derives a JSON schema from
   the C# records (`MealExtraction`, `ExtractedFood`, with `[Description]` attributes that become instructions in the schema) and asks
   the provider for schema-constrained output. Temperature 0, 800 output tokens, a dedicated extraction system prompt, and **no tools**
   in that call.
3. The result is an **untrusted suggestion**: `Validate` requires 1–15 items, names ≤ 100 characters, quantity 0–10,000 and a unit.
   One repair attempt tells the model what was wrong; a second failure raises `meal_parse_failed` (the tool returns a polite error, no proposal).
   JSON is never "repaired" by guessing.
4. The nutrition module validates again (supported units, raw/cooked words), matches foods in the verified database and calculates
   nutrients. Nothing nutritional is ever requested from the model: only what the user said.
5. The extraction prompt treats the text as data: instructions hidden in the meal description are ignored (and evaluated, see below).

Provider mapping: with the OpenAI adapter the request carries `response_format: json_schema` (verified against the local mock). Ollama
uses its `format` parameter through OllamaSharp; support depends on the model.

## Limitations

- Vague quantities ("a handful", "a bowl") become `piece` with quantity 1 or an unsupported unit; the proposal then asks the user for grams.
- Preparation is taken only from the user's wording; when it is missing the matcher chooses by food category and the card says the state was assumed.
- Non-schema-capable models can fail the extraction (reported as a failure, never guessed).
- Two model calls per meal cost latency and tokens.

## How it is tested

- Scripted-model integration tests: malformed text, empty item lists, absurd and negative quantities, valid extraction, unknown foods.
- Live evaluation (`MealExtractionEvaluationTests`, opt-in): six synthetic sentences in English and Romanian including household units, raw
  weights, a missing amount and an instruction hidden in the text. The dataset's shape is validated in every run.
