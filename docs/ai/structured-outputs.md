# Structured outputs

> **Status: designed, not implemented.** Planned for Phase 3 (meal parsing).

## What problem it solves

Free text from a model cannot be fed into code. For meal logging the backend needs a list of
`{ food name, quantity, unit, raw/cooked }` objects. Structured output constrains the model to a JSON schema so the
result can be deserialised and validated.

## Planned design

- Define a C# record (`ParsedMeal { Items: [ParsedFoodItem] }`) and request it with `IChatClient.GetResponseAsync<T>()`,
  which derives a JSON schema from the type and deserialises the answer.
- Treat the result as an **untrusted suggestion**: validate (quantity > 0 and plausible, known unit, known raw/cooked
  value, item count limit), then match each item to the food database deterministically. Unmatched items stay unmatched
  and are shown to the user; nothing is invented ("estimated" entries are explicitly marked).
- Malformed or schema-violating output is an expected failure: one retry with the validation error, then a polite
  failure message. Never "repair" JSON by guessing.
- The LLM extracts; **deterministic code calculates** calories and macronutrients from the food data per 100 g.

## Limitations

Not every model supports schema-constrained decoding; the fallback is JSON-mode plus validation. Quantities in natural
language ("a handful", "a bowl") are ambiguous and must be confirmed by the user, not guessed silently.

## How it will be tested

Table-driven tests with a scripted client returning well-formed, partially wrong, malformed and hostile JSON; property
tests for the nutrient calculator; a labelled set of Romanian and English meal sentences for the live evaluation.
