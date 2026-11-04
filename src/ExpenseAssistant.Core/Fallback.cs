using System.Globalization;
using System.Text.RegularExpressions;

namespace ExpenseAssistant.Core;

/// <summary>
/// Reads common spending questions without a model ("food last month", "how many Uber rides in the last 2 months").
/// Used when the AI is off, busy or over budget, so the app still answers. Less clever than the model, never wrong about maths.
/// </summary>
public static partial class KeywordQuestionParser
{
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["Food"] = ["food", "eat", "eating", "restaurant", "lunch", "dinner", "swiggy", "zomato", "snacks", "tea"],
        ["Groceries"] = ["grocery", "groceries", "vegetables", "supermarket"],
        ["Transport"] = ["transport", "travel", "cab", "taxi", "auto", "fuel", "petrol", "commute"],
        ["Shopping"] = ["shopping", "clothes", "online shopping"],
        ["Bills"] = ["bill", "bills", "recharge", "electricity", "internet"],
        ["Entertainment"] = ["entertainment", "movies", "movie", "subscriptions"],
        ["Health"] = ["health", "medical", "medicine", "medicines", "pharmacy", "doctor", "hospital"],
        ["Rent & EMI"] = ["rent", "emi", "loan"],
        ["Investments"] = ["investment", "investments", "sip", "mutual fund", "stocks"],
    };
    private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m.Length > 0).Select(m => m.ToLowerInvariant()).ToArray();

    [GeneratedRegex(@"last\s+(\d+)\s+months?")] private static partial Regex LastNMonths();
    [GeneratedRegex(@"last\s+(\d+)\s+days?")] private static partial Regex LastNDays();
    [GeneratedRegex(@"top\s+(\d+)")] private static partial Regex TopN();

    public static SpendQuery Parse(string question, DateOnly today, IEnumerable<string> merchants)
    {
        var q = " " + question.ToLowerInvariant() + " ";
        var metric = Has(q, "how many", "how often", "number of", "times", "count") ? "count"
            : Has(q, "top", "where does", "where did", "most", "biggest") ? "top_merchants"
            : Has(q, "by category", "breakdown", "split", "categories") ? "by_category"
            : Has(q, "list", "show me", "which payments") ? "list" : "total";
        var kind = Has(q, "income", "salary", "earned", "received") ? "income" : "spend";
        var category = Synonyms.FirstOrDefault(s => s.Value.Any(w => Regex.IsMatch(q, $@"\b{Regex.Escape(w)}\b"))).Key;
        var merchant = merchants.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => (Full: m, First: m.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant()))
            .Where(m => m.First is { Length: >= 3 } && Regex.IsMatch(q, $@"\b{Regex.Escape(m.First)}\b"))
            .Select(m => m.First).FirstOrDefault();
        if (merchant is not null && category is not null && Synonyms[category].Contains(merchant)) category = null;   // "swiggy" names the payee

        var first = new DateOnly(today.Year, today.Month, 1);
        DateOnly? from = null, to = null;
        if (LastNMonths().Match(q) is { Success: true } lm) { from = first.AddMonths(-int.Parse(lm.Groups[1].Value)); to = today; }
        else if (LastNDays().Match(q) is { Success: true } ld) { from = today.AddDays(-int.Parse(ld.Groups[1].Value) + 1); to = today; }
        else if (Has(q, "last month", "previous month")) { from = first.AddMonths(-1); to = first.AddDays(-1); }
        else if (Has(q, "this month")) { from = first; to = today; }
        else if (Has(q, "this week")) { from = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); to = today; }
        else if (Has(q, "yesterday")) { from = to = today.AddDays(-1); }
        else if (Has(q, "today")) { from = to = today; }
        else if (Has(q, "this year")) { from = new DateOnly(today.Year, 1, 1); to = today; }
        else
        {
            var mi = Array.FindIndex(Months, m => q.Contains(" " + m) || q.Contains(" " + m[..3] + " "));
            if (mi >= 0)
            {
                var year = mi + 1 > today.Month ? today.Year - 1 : today.Year;            // "September" = the last one
                var start = new DateOnly(year, mi + 1, 1);
                if (Has(q, "since", "from", "after")) { from = start; to = today; }
                else { from = start; to = start.AddMonths(1).AddDays(-1); }
            }
        }
        var limit = TopN().Match(q) is { Success: true } tn ? int.Parse(tn.Groups[1].Value) : 5;
        return new SpendQuery(metric, category, merchant, from, to, kind, limit);
    }

    private static bool Has(string q, params string[] words) => words.Any(w => q.Contains(w));
}

/// <summary>
/// Caps AI calls so a public demo cannot burn the free quota: a few per minute and a daily total. Over the cap the app
/// answers with the rules instead. Thread-safe; time comes from <see cref="TimeProvider"/> so tests can move it.
/// </summary>
public sealed class AiBudget(TimeProvider clock, int perMinute = 6, int perDay = 200)
{
    private readonly Lock gate = new();
    private readonly Queue<DateTimeOffset> recent = new();
    private DateOnly day;
    private int today;

    public bool TryTake()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var d = DateOnly.FromDateTime(now.UtcDateTime);
            if (d != day) { day = d; today = 0; }
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
            if (recent.Count >= perMinute || today >= perDay) return false;
            recent.Enqueue(now);
            today++;
            return true;
        }
    }

    public (int LastMinute, int Today, int PerMinute, int PerDay) Usage
    {
        get { lock (gate) return (recent.Count, today, perMinute, perDay); }
    }
}
