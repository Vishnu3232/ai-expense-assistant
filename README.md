# AI Expense Assistant (.NET 10)

**Live demo:** https://ai-expense-assistant-g0ib.onrender.com (free hosting: the first visit after a quiet spell can take
about a minute to wake up; the AI has a small daily free quota, after which keyword rules answer and the page says so).

An ASP.NET Core API and Blazor page that categorises Indian bank transactions with an LLM and answers plain-language questions about
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
- **Safe to put online.** `AiBudget` caps model calls per minute and per day, and the API is rate limited per client.
  Over the cap, or when the model is busy, `KeywordQuestionParser` reads common questions with rules, so the demo
  always answers and says which path was used.
- **Blazor UI.** A chat-style page (interactive server rendering) shows the answer, how it was read (AI or rules), the
  query the question became, and each transaction next to the masked text the model would see.
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
`gemini-3.5-flash-lite`, which has the largest free quota), `Endpoint`, `PerMinute` / `PerDay` (AI call caps). Google
retires model names for new keys, so the model is configuration, not code.

## Deploy

`Dockerfile` builds, runs the tests and publishes the API. `render.yaml` is a Render blueprint (free plan); set
`AI__ApiKey` in the host's secrets. Behind a proxy the app reads the forwarded client address for its rate limit.
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
