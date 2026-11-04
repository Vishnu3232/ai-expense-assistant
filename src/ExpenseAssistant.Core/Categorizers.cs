using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Core;

public interface ICategorizer
{
    /// <summary>Returns a category for every transaction id given (unknown ones fall back to "Other").</summary>
    Task<IReadOnlyDictionary<int, string>> CategorizeAsync(IReadOnlyList<Transaction> txs, CancellationToken ct = default);
}

/// <summary>Deterministic whole-word keyword rules: fast, free, and the fallback when the AI is unavailable.</summary>
public sealed class RuleCategorizer : ICategorizer
{
    private static readonly (string Category, string[] Words)[] Rules =
    [
        ("Food", ["swiggy", "zomato", "restaurant", "hotel", "cafe", "bakery", "tea", "biryani", "dominos", "kfc"]),
        ("Groceries", ["supermarket", "super market", "bigbasket", "blinkit", "zepto", "dmart", "grocery", "milk"]),
        ("Transport", ["uber", "ola", "rapido", "irctc", "metro", "petrol", "fuel", "fastag", "redbus"]),
        ("Shopping", ["amazon", "flipkart", "myntra", "meesho", "ajio", "silks", "textiles"]),
        ("Bills", ["airtel", "jio", "electricity", "kseb", "broadband", "recharge", "gas"]),
        ("Entertainment", ["netflix", "spotify", "hotstar", "bookmyshow", "pvr"]),
        ("Health", ["pharmacy", "apollo", "hospital", "clinic", "medical", "lab"]),
        ("Rent & EMI", ["rent", "emi", "loan"]),
        ("Investments", ["zerodha", "groww", "mutual fund", "sip"]),
    ];

    private static readonly Dictionary<string, Regex> Compiled = Rules.ToDictionary(
        r => r.Category,
        r => new Regex(@"(?<![a-z0-9])(?:" + string.Join("|", r.Words.Select(Regex.Escape)) + @")(?:e?s)?(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled));

    public static string Guess(Transaction t)
    {
        foreach (var (category, rx) in Compiled)
            if (rx.IsMatch(t.Merchant) || rx.IsMatch(t.Narration)) return category;
        return Categories.Uncategorized;
    }

    public Task<IReadOnlyDictionary<int, string>> CategorizeAsync(IReadOnlyList<Transaction> txs, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(txs.ToDictionary(t => t.Id, Guess));
}

/// <summary>
/// Asks the model (any <see cref="IChatClient"/>: a self-hosted Llama, Gemini, Azure OpenAI...) to label transactions.
/// It labels each distinct payee once, not each row: 58 rows with 13 payees is one small call, every SWIGGY row gets
/// the same answer, and a small model copes far better. Only masked text goes out; answers outside the allowed list,
/// or a failed call, fall back to the rules.
/// </summary>
public sealed class AiCategorizer(IChatClient chat, int batchSize = 40, AiBudget? budget = null, Action<Exception>? onError = null) : ICategorizer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Item(int Id, string Category);
    public sealed record Payee(int Id, string Name, string Sample, int Payments, decimal TypicalAmount, string Kind, IReadOnlyList<Transaction> Rows);

    /// <summary>How many rows the last run labelled through the model, and how many fell back to the rules (so a silent outage shows).</summary>
    public int LastAiCount { get; private set; }
    public int LastRuleCount { get; private set; }
    public int LastCalls { get; private set; }

    public static string PayeeKey(Transaction t) => Regex.Replace(PiiMasker.Mask(t.Merchant).ToUpperInvariant(), @"\s+", " ").Trim();

    public static List<Payee> GroupPayees(IEnumerable<Transaction> txs) =>
        txs.GroupBy(PayeeKey).Select((g, i) =>
        {
            var rows = g.ToList();
            var amounts = rows.Select(r => r.Amount).OrderBy(a => a).ToList();
            return new Payee(i + 1, g.Key, PiiMasker.Mask(rows[0].Narration), rows.Count, amounts[amounts.Count / 2],
                rows.Count(r => r.Kind == TxKind.Credit) > rows.Count / 2 ? "credit" : "debit", rows);
        }).ToList();

    public async Task<IReadOnlyDictionary<int, string>> CategorizeAsync(IReadOnlyList<Transaction> txs, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        int byAi = 0, byRules = 0, calls = 0;
        foreach (var batch in GroupPayees(txs).Chunk(batchSize))
        {
            Dictionary<int, string> answered = [];
            if (budget is null || budget.TryTake()) { calls++; answered = await AskAsync(batch, ct); }   // over the cap: rules
            foreach (var p in batch)
                foreach (var t in p.Rows)
                {
                    if (answered.TryGetValue(p.Id, out var c)) { result[t.Id] = c; byAi++; }
                    else { result[t.Id] = RuleCategorizer.Guess(t); byRules++; }
                }
        }
        (LastAiCount, LastRuleCount, LastCalls) = (byAi, byRules, calls);
        return result;
    }

    /// <summary>The prompt exactly as sent, so tests (and you) can check that nothing personal leaks.</summary>
    public static string BuildPrompt(IEnumerable<Payee> batch) =>
        "Each line is a payee from an Indian bank account (UPI/NEFT alerts), with a sample of the bank's text (ids are masked). " +
        "Give each payee exactly one category from: " + string.Join(", ", Categories.All) + ".\n" +
        "Guide: food delivery, restaurants, tea stalls = Food; supermarkets, vegetables, milk = Groceries; cabs, fuel, trains = Transport; " +
        "online shops = Shopping; phone recharge, electricity, internet = Bills; pharmacy, hospital = Health; rent, loan EMI = Rent & EMI; " +
        "brokers, SIP = Investments; a person's name with no other hint = Other; a company paying a salary = Other.\n" +
        "Return only a JSON object: {\"labels\": [{\"id\": 1, \"category\": \"Food\"}, {\"id\": 2, \"category\": \"Other\"}]}.\n" +
        string.Join("\n", batch.Select(p => JsonSerializer.Serialize(new { id = p.Id, payee = p.Name, sample = p.Sample, payments = p.Payments, typical_amount = p.TypicalAmount, kind = p.Kind }, Json)));

    private async Task<Dictionary<int, string>> AskAsync(Payee[] batch, CancellationToken ct)
    {
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "You categorise personal bank transactions. Answer with JSON only."),
                 new ChatMessage(ChatRole.User, BuildPrompt(batch))],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 }, ct);
            var items = ParseLabels(response.Text);
            var ids = batch.Select(b => b.Id).ToHashSet();
            return items.Where(i => ids.Contains(i.Id) && Categories.Normalize(i.Category) is not null)
                        .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => Categories.Normalize(g.First().Category)!);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            onError?.Invoke(e);
            return [];      // model down, quota hit or a broken answer: the rules decide for this batch
        }
    }

    /// <summary>
    /// Reads the labels however the model shaped them: a bare array, an object wrapping the array ("json mode" on open
    /// models does this), ids as numbers or strings, and "category" or "label" as the key.
    /// </summary>
    public static List<Item> ParseLabels(string? text)
    {
        using var doc = JsonDocument.Parse(JsonText.Extract(text));
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array)
                : default;
        var list = new List<Item>();
        // a single {"id": 1, "category": "Food"} with no array around it counts as a one-item answer
        var elements = array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out _) ? [root] : [];
        foreach (var e in elements)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            int? id = null;
            if (e.TryGetProperty("id", out var idEl))
                id = idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var n) ? n
                    : idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var m) ? m : null;
            var cat = e.TryGetProperty("category", out var c) ? c : e.TryGetProperty("label", out var l) ? l : default;
            if (id is not null && cat.ValueKind == JsonValueKind.String) list.Add(new Item(id.Value, cat.GetString()!));
        }
        return list;
    }
}

internal static class JsonText
{
    /// <summary>Models sometimes wrap JSON in ```json fences or add a sentence; keep only the JSON part.</summary>
    public static string Extract(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        var start = s.IndexOfAny(['[', '{']);
        var end = Math.Max(s.LastIndexOf(']'), s.LastIndexOf('}'));
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }
}
