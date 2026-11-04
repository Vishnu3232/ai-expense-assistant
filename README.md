# AI Expense Assistant (.NET 10)

**Live demo:** https://ai-expense-assistant-g0ib.onrender.com (free hosting: the first visit after a quiet spell can take
about a minute to wake up; the AI has a small daily free quota, after which keyword rules answer and the page says so).

An ASP.NET Core API and Blazor page that categorises Indian bank transactions with an LLM and answers plain-language questions about
spending ("How much did I spend on food last month?"). It grew out of a personal expense tracker I use every day; this
repository is the AI layer, rebuilt in .NET with tests and made-up sample data.

## What it shows

- **Provider-neutral AI, with a fallback chain.** Everything talks to `Microsoft.Extensions.AI.IChatClient`. The live
  demo runs a self-hosted Llama 3.1 8B (Ollama on a RunPod GPU) first, Google Gemini when that is down, and keyword
  rules when neither answers. Each answer says which model read it. Any OpenAI-compatible endpoint can be added in
  configuration (`AI:Providers:N`).
- **Labels per payee, not per row.** `AiCategorizer` groups rows by masked payee and sends one line per payee: 58 rows
  with 13 payees is one small call, identical payees always get the same label, and a small model copes well
  (on the 8B model this went from 4 minutes and inconsistent labels to 4 seconds and all 13 payees right).
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

Several models in order (first that answers wins):

```
AI__Providers__0__Name=RunPod   AI__Providers__0__Endpoint=https://<pod>-11434.proxy.runpod.net/v1/   AI__Providers__0__Model=llama3.1:8b   AI__Providers__0__ApiKey=ollama
AI__Providers__1__Name=Gemini   AI__Providers__1__Model=gemini-3.5-flash-lite   AI__Providers__1__ApiKey=<key>
```

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
