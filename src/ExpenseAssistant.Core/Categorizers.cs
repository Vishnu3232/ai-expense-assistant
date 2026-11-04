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
/// Asks the model (any <see cref="IChatClient"/>: Gemini, Azure OpenAI, Ollama...) to label transactions.
/// Only masked text goes out; answers outside the allowed list, or a failed call, fall back to the rules.
/// </summary>
public sealed class AiCategorizer(IChatClient chat, int batchSize = 40) : ICategorizer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Item(int Id, string Category);

    /// <summary>How many labels the last run got from the model, and how many fell back to the rules (so a silent outage shows).</summary>
    public int LastAiCount { get; private set; }
    public int LastRuleCount { get; private set; }

    public async Task<IReadOnlyDictionary<int, string>> CategorizeAsync(IReadOnlyList<Transaction> txs, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        int byAi = 0, byRules = 0;
        foreach (var batch in txs.Chunk(batchSize))
        {
            var answered = await AskAsync(batch, ct);
            foreach (var t in batch)
            {
                if (answered.TryGetValue(t.Id, out var c)) { result[t.Id] = c; byAi++; }
                else { result[t.Id] = RuleCategorizer.Guess(t); byRules++; }
            }
        }
        (LastAiCount, LastRuleCount) = (byAi, byRules);
        return result;
    }

    /// <summary>The prompt exactly as sent, so tests (and you) can check that nothing personal leaks.</summary>
    public static string BuildPrompt(IEnumerable<Transaction> batch) =>
        "Label each Indian bank transaction with exactly one category from this list: " + string.Join(", ", Categories.All) + ".\n" +
        "Payments to a person's name with no other hint are \"Other\". Return only JSON: [{\"id\": 1, \"category\": \"Food\"}].\n" +
        string.Join("\n", batch.Select(t => JsonSerializer.Serialize(new
        {
            id = t.Id, kind = t.Kind.ToString().ToLowerInvariant(), amount = t.Amount,
            merchant = PiiMasker.Mask(t.Merchant), text = PiiMasker.Mask(t.Narration),
        }, Json)));

    private async Task<Dictionary<int, string>> AskAsync(Transaction[] batch, CancellationToken ct)
    {
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "You categorise personal bank transactions. Answer with JSON only."),
                 new ChatMessage(ChatRole.User, BuildPrompt(batch))],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 }, ct);
            var items = JsonSerializer.Deserialize<List<Item>>(JsonText.Extract(response.Text), Json) ?? [];
            var ids = batch.Select(b => b.Id).ToHashSet();
            return items.Where(i => ids.Contains(i.Id) && Categories.Normalize(i.Category) is not null)
                        .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => Categories.Normalize(g.First().Category)!);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];      // model down, quota hit or a broken answer: the rules decide for this batch
        }
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
