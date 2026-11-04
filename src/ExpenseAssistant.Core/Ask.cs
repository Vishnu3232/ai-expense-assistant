using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Core;

/// <summary>
/// A question turned into a small, checkable query. The model only writes this; the numbers are worked out in C# over
/// your data, so the model never sees transactions and cannot make up a total.
/// </summary>
public sealed record SpendQuery(
    string Metric,                 // "total" | "count" | "top_merchants" | "by_category" | "list"
    string? Category = null,
    string? Merchant = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string Kind = "spend",         // "spend" | "income"
    int Limit = 5)
{
    public static readonly string[] Metrics = ["total", "count", "top_merchants", "by_category", "list"];
}

public sealed record AskAnswer(string Answer, SpendQuery? Query, IReadOnlyList<Transaction> Rows, bool UsedAi, string? Note = null, string? Model = null);

public static class QueryEngine
{
    public static IEnumerable<Transaction> Filter(IEnumerable<Transaction> txs, SpendQuery q) =>
        txs.Where(t => q.Kind == "income"
                ? t.Kind == TxKind.Credit && t.Category != "Transfers"
                : t.Kind == TxKind.Debit && !Categories.NonSpend.Contains(t.Category))
           .Where(t => q.Category is null || string.Equals(t.Category, q.Category, StringComparison.OrdinalIgnoreCase))
           .Where(t => q.Merchant is null || t.Merchant.Contains(q.Merchant, StringComparison.OrdinalIgnoreCase))
           .Where(t => q.From is null || t.Date >= q.From)
           .Where(t => q.To is null || t.Date <= q.To);

    public static AskAnswer Run(IReadOnlyList<Transaction> txs, SpendQuery q, bool usedAi)
    {
        var rows = Filter(txs, q).OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToList();
        var what = Describe(q);
        var verb = q.Kind == "income" ? "received" : "spent";
        string answer = q.Metric switch
        {
            "count" => $"{rows.Count} payments {what}.",
            "top_merchants" => rows.Count == 0 ? $"Nothing {what}." :
                $"Top payees {what}: " + string.Join(", ", rows.GroupBy(t => t.Merchant)
                    .Select(g => (g.Key, Total: g.Sum(t => t.Amount))).OrderByDescending(x => x.Total).Take(q.Limit)
                    .Select(x => $"{x.Key} {Inr(x.Total)}")) + ".",
            "by_category" => rows.Count == 0 ? $"Nothing {what}." :
                $"By category {what}: " + string.Join(", ", rows.GroupBy(t => t.Category)
                    .Select(g => (g.Key, Total: g.Sum(t => t.Amount))).OrderByDescending(x => x.Total)
                    .Select(x => $"{x.Key} {Inr(x.Total)}")) + ".",
            "list" => rows.Count == 0 ? $"No payments {what}." : $"{rows.Count} payments {what}; latest first.",
            _ => $"You {verb} {Inr(rows.Sum(t => t.Amount))} {what} ({rows.Count} payments).",
        };
        return new AskAnswer(answer, q, q.Metric == "list" ? rows.Take(Math.Max(1, q.Limit)).ToList() : rows.Take(20).ToList(), usedAi);
    }

    public static string Inr(decimal v) => "₹" + v.ToString("#,##,##0.##", CultureInfo.GetCultureInfo("en-IN"));

    private static string Describe(SpendQuery q)
    {
        var parts = new List<string>();
        if (q.Category is not null) parts.Add($"on {q.Category}");
        if (q.Merchant is not null) parts.Add($"at {q.Merchant}");
        if (q.From is not null && q.To is not null) parts.Add($"from {q.From:d MMM yyyy} to {q.To:d MMM yyyy}");
        else if (q.From is not null) parts.Add($"since {q.From:d MMM yyyy}");
        else if (q.To is not null) parts.Add($"until {q.To:d MMM yyyy}");
        return parts.Count == 0 ? "in all" : string.Join(" ", parts);
    }
}

/// <summary>Turns a plain-language question into a <see cref="SpendQuery"/> with the model, then answers it locally.</summary>
public sealed class AskService(IChatClient? chat, TimeProvider clock, Action<Exception>? onError = null, AiBudget? budget = null)
{
    /// <summary>Reads the model's JSON leniently: nulls, "5" for 5, dates with a time part, missing fields.</summary>
    public static SpendQuery ParseQuery(string text)
    {
        using var doc = JsonDocument.Parse(JsonText.Extract(text));
        var o = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
        string? Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            && !string.Equals(v.GetString(), "null", StringComparison.OrdinalIgnoreCase) ? v.GetString()!.Trim() : null;
        DateOnly? Date(string name) => Str(name) is { } s && DateOnly.TryParse(s.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, out var d) ? d : null;
        int limit = 5;
        if (o.TryGetProperty("limit", out var l))
        {
            if (l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n)) limit = n;
            else if (l.ValueKind == JsonValueKind.String && int.TryParse(l.GetString(), out var m)) limit = m;
        }
        return new SpendQuery(Str("metric") ?? "total", Str("category"), Str("merchant"), Date("from"), Date("to"), Str("kind") ?? "spend", limit);
    }

    public static string BuildPrompt(string question, DateOnly today) =>
        $"Today is {today:yyyy-MM-dd} (India). Turn the question about personal spending into JSON with these fields:\n" +
        $"metric: one of {string.Join(", ", SpendQuery.Metrics)}; category: one of {string.Join(", ", Categories.All)} or null; " +
        "merchant: a payee name if one is named, else null; from, to: dates as yyyy-MM-dd or null (\"last month\" = the whole " +
        "previous calendar month; \"this month\" = from the 1st of the current month to today; \"since September\" = 1 September " +
        "to today); kind: spend or income; limit: number of items for top_merchants or list. \"How much\" and \"what did I spend\" " +
        "mean metric total; \"how many\" means count. Keep a named payee in merchant (\"at Apollo\" -> merchant: \"Apollo\").\n" +
        "Return only the JSON object.\nQuestion: " + question;

    public async Task<AskAnswer> AskAsync(string question, IReadOnlyList<Transaction> txs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return new AskAnswer("Ask something like \"How much did I spend on food last month?\"", null, [], false);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (chat is null) return Fallback("AI is not configured");
        if (budget is not null && !budget.TryTake()) return Fallback("AI limit for this demo reached");
        SpendQuery? q;
        string? modelId;
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "You convert spending questions into a JSON query. Never invent numbers."),
                 new ChatMessage(ChatRole.User, BuildPrompt(question, today))],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 }, ct);
            q = ParseQuery(response.Text);
            modelId = response.ModelId;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            onError?.Invoke(e);
            return Fallback(e.Message.Contains("429") || e.Message.Contains("503") ? "AI busy (free-tier limit)" : "AI answer unreadable");
        }
        return QueryEngine.Run(txs, Validate(q), usedAi: true) with { Model = modelId };

        // the model is not available: read the question with keywords, still answer from the data
        AskAnswer Fallback(string why)
        {
            var a = QueryEngine.Run(txs, Validate(KeywordQuestionParser.Parse(question, today, txs.Select(t => t.Merchant))), usedAi: false);
            return a with { Note = why + "; answered with keyword rules" };
        }
    }

    /// <summary>Never trust model output: unknown metric/category/kind are reset, dates are put in order.</summary>
    public static SpendQuery Validate(SpendQuery? q)
    {
        q ??= new SpendQuery("total");
        var metric = SpendQuery.Metrics.Contains(q.Metric?.ToLowerInvariant()) ? q.Metric!.ToLowerInvariant() : "total";
        var (from, to) = q.From > q.To ? (q.To, q.From) : (q.From, q.To);
        return q with
        {
            Metric = metric, Category = Categories.Normalize(q.Category), Kind = q.Kind?.ToLowerInvariant() == "income" ? "income" : "spend",
            Merchant = string.IsNullOrWhiteSpace(q.Merchant) ? null : q.Merchant.Trim(), From = from, To = to, Limit = Math.Clamp(q.Limit, 1, 50),
        };
    }
}
