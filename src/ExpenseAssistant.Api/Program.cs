using System.ClientModel;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuleCategorizer>();

// Any OpenAI-compatible model works behind IChatClient. Default: Google Gemini (free tier) via its OpenAI endpoint.
// The key comes from user-secrets or the environment (AI__ApiKey), never from a file in the repository.
var ai = builder.Configuration.GetSection("AI");
var apiKey = ai["ApiKey"];
// Google retires model names for new keys (gemini-2.5-flash answers 404 now), so the name lives in configuration.
var model = ai["Model"] ?? "gemini-3.5-flash";
if (!string.IsNullOrWhiteSpace(apiKey))
{
    builder.Services.AddChatClient(_ => new OpenAIClient(new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(ai["Endpoint"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/") })
        .GetChatClient(model).AsIChatClient())
        .UseLogging();
}
builder.Services.AddSingleton<ICategorizer>(sp => sp.GetService<IChatClient>() is { } chat
    ? new AiCategorizer(chat)
    : sp.GetRequiredService<RuleCategorizer>());
builder.Services.AddSingleton(sp => new AskService(sp.GetService<IChatClient>(), sp.GetRequiredService<TimeProvider>(),
    e => sp.GetRequiredService<ILogger<AskService>>().LogWarning(e, "Ask failed")));
builder.Services.AddSingleton(sp => new TransactionStore(
    SampleData.Create(DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetLocalNow().DateTime))));

var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapGet("/api/status", (IServiceProvider sp) => new { ai = sp.GetService<IChatClient>() is not null, model });

app.MapGet("/api/transactions", (TransactionStore store, string? category) =>
    store.All.Where(t => category is null || t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Date));

// Label every transaction (AI when configured, rules otherwise) and keep the result.
app.MapPost("/api/categorize", async (TransactionStore store, ICategorizer categorizer, CancellationToken ct) =>
{
    var labels = await categorizer.CategorizeAsync(store.All, ct);
    store.Apply(labels);
    return new
    {
        categorized = labels.Count, by = categorizer.GetType().Name,
        byAi = (categorizer as AiCategorizer)?.LastAiCount, byRules = (categorizer as AiCategorizer)?.LastRuleCount,
        byCategory = store.All.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.Count()),
    };
});

app.MapPost("/api/ask", async (AskRequest req, TransactionStore store, AskService ask, CancellationToken ct) =>
    Results.Ok(await ask.AskAsync(req.Question, store.All, ct)));

app.Run();

public sealed record AskRequest(string Question);

/// <summary>In-memory transactions for the demo (a real deployment would use a database).</summary>
public sealed class TransactionStore(IReadOnlyList<Transaction> seed)
{
    private readonly Lock gate = new();
    private IReadOnlyList<Transaction> rows = seed;

    public IReadOnlyList<Transaction> All { get { lock (gate) return rows; } }

    public void Apply(IReadOnlyDictionary<int, string> labels)
    {
        lock (gate) rows = rows.Select(t => labels.TryGetValue(t.Id, out var c) ? t with { Category = c } : t).ToList();
    }
}

public partial class Program;     // lets the tests start the API in memory
