# AI Expense Assistant (.NET 10)

An ASP.NET Core API that categorises Indian bank transactions with an LLM and answers plain-language questions about
spending ("How much did I spend on food last month?"). It grew out of a personal expense tracker I use every day; this
repository is the AI layer, rebuilt in .NET with tests and made-up sample data.

## What it shows

- **Provider-neutral AI.** Everything talks to `Microsoft.Extensions.AI.IChatClient`. The default is Google Gemini
  through its OpenAI-compatible endpoint; Azure OpenAI, OpenAI or a local Ollama model is a configuration change.
- **Privacy by design.** `PiiMasker` removes UPI ids, phone numbers, card, account and IFSC numbers before anything is
  sent to a model. A test checks the outgoing prompts.
- **The model never does the maths.** `/api/ask` has the model turn the question into a small `SpendQuery`
  (metric, category, merchant, date range). The query is validated, then run in C# over the data. The model sees no
  transactions and cannot invent a total.
- **Graceful degradation.** Unknown labels, malformed JSON, quota errors or no API key fall back to deterministic
  keyword rules. The app works with no AI at all.
- **Tested without a network.** A scripted `IChatClient` drives unit tests and in-memory API tests
  (`WebApplicationFactory`). The test run needs no API key.

## Run it

```bash
dotnet test
cd src/ExpenseAssistant.Api
dotnet user-secrets set "AI:ApiKey" "<your Gemini API key from aistudio.google.com>"
dotnet run
```

| Endpoint | What it does |
|---|---|
| `GET /api/status` | Whether an AI model is configured |
| `GET /api/transactions?category=Food` | The sample transactions |
| `POST /api/categorize` | Labels every transaction (AI, or rules without a key) |
| `POST /api/ask` `{"question": "..."}` | Answers a spending question |

Settings (`AI` section in appsettings.json, or `AI__*` environment variables): `ApiKey`, `Model` (default
`gemini-3.5-flash`), `Endpoint`. Google retires model names for new keys, so the model is configuration, not code.
`POST /api/categorize` reports how many labels came from the model and how many fell back to the rules.

## Layout

```
src/ExpenseAssistant.Core   domain, PiiMasker, RuleCategorizer, AiCategorizer, AskService + QueryEngine, SampleData
src/ExpenseAssistant.Api    minimal API, DI wiring of IChatClient
tests/ExpenseAssistant.Tests unit tests and in-memory API tests with a fake model
```

## Note on data

All data in this repository is generated (`SampleData`). On the Gemini free tier, prompts may be used by Google to
improve its products, so only masked or sample data should be sent.
