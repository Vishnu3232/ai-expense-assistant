using System.ClientModel;
using System.Threading.RateLimiting;
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
// A public demo shares one free key: cap AI calls (over the cap, keyword rules answer instead).
builder.Services.AddSingleton(sp => new AiBudget(sp.GetRequiredService<TimeProvider>(),
    ai.GetValue("PerMinute", 6), ai.GetValue("PerDay", 200)));
builder.Services.AddSingleton(sp => new AiStatus(sp.GetService<IChatClient>() is not null, model));
builder.Services.AddSingleton<ICategorizer>(sp => sp.GetService<IChatClient>() is { } chat
    ? new AiCategorizer(chat, budget: sp.GetRequiredService<AiBudget>())
    : sp.GetRequiredService<RuleCategorizer>());
builder.Services.AddSingleton(sp => new AskService(sp.GetService<IChatClient>(), sp.GetRequiredService<TimeProvider>(),
    e => sp.GetRequiredService<ILogger<AskService>>().LogWarning(e, "Ask failed"), sp.GetRequiredService<AiBudget>()));
builder.Services.AddSingleton(sp => new TransactionStore(
    SampleData.Create(DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetLocalNow().DateTime))
        .Select(t => t with { Category = RuleCategorizer.Guess(t) }).ToList()));      // shown labelled; "Label with AI" relabels
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-ip", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// Behind a host's proxy (Render, Azure) every request comes from the proxy: read the client address it forwards,
// or the per-client rate limit would be shared by everyone.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
app.UseForwardedHeaders();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
else app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<ExpenseAssistant.Api.Components.App>().AddInteractiveServerRenderMode();

app.MapGet("/api/status", (AiStatus s, AiBudget b) => new { ai = s.Enabled, model = s.Model, usage = new { b.Usage.LastMinute, b.Usage.Today } });

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
}).RequireRateLimiting("per-ip");

app.MapPost("/api/ask", async (AskRequest req, TransactionStore store, AskService ask, CancellationToken ct) =>
    req.Question is { Length: > 200 } ? Results.BadRequest("Keep questions under 200 characters.")
        : Results.Ok(await ask.AskAsync(req.Question ?? "", store.All, ct))).RequireRateLimiting("per-ip");

app.Run();

public sealed record AskRequest(string? Question);
public sealed record AiStatus(bool Enabled, string Model);

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
