using System.Runtime.CompilerServices;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

/// <summary>A scripted model: answers with canned text and records every prompt it was sent.</summary>
internal sealed class FakeChatClient(Func<string, string> answer) : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var prompt = string.Join("\n", messages.Select(m => m.Text));
        Prompts.Add(prompt);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(prompt))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public class PiiMaskerTests
{
    [Theory]
    [InlineData("UPI-RAVI KUMAR-ravikumar1987@okaxis-SBIN0001234-615123456789-RENT", "UPI-RAVI KUMAR-[upi]-[ifsc]-[num]-RENT")]
    [InlineData("Paid from 9876543210 and +91 9123456780", "Paid from [phone] and [phone]")]
    [InlineData("Card XX1234 used at SWIGGY", "Card [card] used at SWIGGY")]
    [InlineData("", "")]
    public void Masks_ids_but_keeps_merchant_words(string input, string expected) => Assert.Equal(expected, PiiMasker.Mask(input));
}

public class RuleCategorizerTests
{
    private static Transaction Tx(string merchant, string narration = "") => new(1, new DateOnly(2026, 11, 1), 100, TxKind.Debit, merchant, narration);

    [Theory]
    [InlineData("SWIGGY", "Food")]
    [InlineData("APOLLO PHARMACY", "Health")]
    [InlineData("CRED CLUB", "Other")]                  // whole words only: "cred" must not hit "credit"
    [InlineData("JIO PREPAID", "Bills")]
    [InlineData("RAVI KUMAR", "Other")]
    public void Whole_word_keywords(string merchant, string expected) => Assert.Equal(expected, RuleCategorizer.Guess(Tx(merchant)));
}

public class AiCategorizerTests
{
    private static readonly IReadOnlyList<Transaction> Data = SampleData.Create(new DateOnly(2026, 11, 20), months: 1);

    [Fact]
    public async Task Uses_the_model_answer_when_it_is_valid()
    {
        var fake = new FakeChatClient(p => "```json\n[" + string.Join(",", Data.Select(t => $"{{\"id\":{t.Id},\"category\":\"{(t.Merchant == "RAVI KUMAR" ? "Family" : "food")}\"}}")) + "]\n```");
        var labels = await new AiCategorizer(fake).CategorizeAsync(Data);
        Assert.Equal(Data.Count, labels.Count);
        Assert.All(Data.Where(t => t.Merchant == "RAVI KUMAR"), t => Assert.Equal("Family", labels[t.Id]));
        Assert.All(Data.Where(t => t.Merchant != "RAVI KUMAR"), t => Assert.Equal("Food", labels[t.Id]));   // "food" normalised
    }

    [Fact]
    public async Task Sends_no_upi_ids_phone_or_account_numbers()
    {
        var fake = new FakeChatClient(_ => "[]");
        await new AiCategorizer(fake).CategorizeAsync(Data);
        var sent = string.Join("\n", fake.Prompts);
        Assert.DoesNotContain("@okaxis", sent);
        Assert.DoesNotContain("@ybl", sent);
        Assert.DoesNotMatch(@"(?<!\d)[6-9]\d{9}(?!\d)", sent);
        Assert.Contains("SWIGGY", sent);
    }

    [Fact]
    public async Task Falls_back_to_rules_on_unknown_labels_errors_and_missing_ids()
    {
        var bad = new FakeChatClient(_ => "[{\"id\": 1, \"category\": \"Crypto\"}, {\"id\": 999, \"category\": \"Food\"}]");
        var labels = await new AiCategorizer(bad).CategorizeAsync(Data);
        Assert.All(Data, t => Assert.Equal(RuleCategorizer.Guess(t), labels[t.Id]));

        var broken = new FakeChatClient(_ => throw new HttpRequestException("429 quota"));
        var cat = new AiCategorizer(broken);
        labels = await cat.CategorizeAsync(Data);
        Assert.All(Data, t => Assert.Equal(RuleCategorizer.Guess(t), labels[t.Id]));
        Assert.Equal((0, Data.Count), (cat.LastAiCount, cat.LastRuleCount));     // the outage is visible, not silent
    }

    [Fact]
    public async Task Sends_big_lists_in_batches()
    {
        var fake = new FakeChatClient(_ => "[]");
        await new AiCategorizer(fake, batchSize: 10).CategorizeAsync(Data);
        Assert.Equal((Data.Count + 9) / 10, fake.Prompts.Count);
    }
}

public class AskTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly IReadOnlyList<Transaction> Data = SampleData.Create(Today, months: 3)
        .Select(t => t with { Category = RuleCategorizer.Guess(t) }).ToList();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }

    [Fact]
    public async Task Model_writes_the_query_the_numbers_come_from_csharp()
    {
        var fake = new FakeChatClient(_ => "{\"metric\":\"total\",\"category\":\"Food\",\"from\":\"2026-10-01\",\"to\":\"2026-10-31\",\"kind\":\"spend\"}");
        var ask = new AskService(fake, new FixedClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));
        var a = await ask.AskAsync("How much did I spend on food last month?", Data);
        var expected = Data.Where(t => t.Kind == TxKind.Debit && t.Category == "Food" && t.Date.Month == 10).Sum(t => t.Amount);
        Assert.Contains(QueryEngine.Inr(expected), a.Answer);
        Assert.True(a.UsedAi);
        Assert.Contains("Today is 2026-11-20", fake.Prompts.Single());
        Assert.DoesNotContain("SWIGGY", fake.Prompts.Single());          // no transactions are sent to the model
    }

    [Theory]
    [InlineData("{\"metric\":\"count\",\"merchant\":\"Uber\",\"from\":\"2026-09-20\",\"to\":null,\"limit\":null}", "count", "Uber", "2026-09-20", 5)]
    [InlineData("```json\n{\"metric\":\"total\",\"category\":\"Health\",\"from\":\"2026-09-01T00:00:00Z\",\"limit\":\"3\"}\n```", "total", null, "2026-09-01", 3)]
    [InlineData("[{\"metric\":\"list\",\"merchant\":\"null\"}]", "list", null, null, 5)]
    public void Model_json_is_read_leniently(string json, string metric, string? merchant, string? from, int limit)
    {
        var q = AskService.ParseQuery(json);
        Assert.Equal((metric, merchant, from is null ? (DateOnly?)null : DateOnly.Parse(from), limit), (q.Metric, q.Merchant, q.From, q.Limit));
    }

    [Fact]
    public void Bad_model_output_is_made_safe()
    {
        var q = AskService.Validate(new SpendQuery("DROP TABLE", Category: "crypto", From: new DateOnly(2026, 11, 30), To: new DateOnly(2026, 11, 1), Kind: "x", Limit: 1000));
        Assert.Equal(("total", null, "spend", 50), (q.Metric, q.Category, q.Kind, q.Limit));
        Assert.True(q.From < q.To);
    }

    [Fact]
    public void Spend_leaves_out_transfers_and_investments()
    {
        var a = QueryEngine.Run(Data, new SpendQuery("by_category"), usedAi: false);
        Assert.DoesNotContain("Investments", a.Answer);
        Assert.Contains("Rent & EMI", a.Answer);
    }

    [Fact]
    public async Task Without_a_key_keyword_rules_still_answer()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var a = await new AskService(null, clock).AskAsync("How much did I spend on food last month?", Data);
        var expected = Data.Where(t => t.Kind == TxKind.Debit && t.Category == "Food" && t.Date.Month == 10).Sum(t => t.Amount);
        Assert.Contains(QueryEngine.Inr(expected), a.Answer);
        Assert.False(a.UsedAi);
        Assert.Contains("keyword rules", a.Note);
    }

    [Fact]
    public async Task Busy_model_falls_back_to_keyword_rules()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var busy = new FakeChatClient(_ => throw new HttpRequestException("Status: 429 (Too Many Requests)"));
        var a = await new AskService(busy, clock).AskAsync("How many times did I take Uber in the last 2 months?", Data);
        Assert.Equal(("count", "uber"), (a.Query!.Metric, a.Query.Merchant));
        Assert.Contains("AI busy", a.Note);
    }

    [Theory]
    [InlineData("How much did I spend on food last month?", "total", "Food", null, "2026-10-01", "2026-10-31")]
    [InlineData("top 3 payees this month", "top_merchants", null, null, "2026-11-01", "2026-11-20")]
    [InlineData("What did I spend at Apollo since September?", "total", null, "apollo", "2026-09-01", "2026-11-20")]
    [InlineData("how many swiggy orders in the last 2 months", "count", null, "swiggy", "2026-09-01", "2026-11-20")]
    [InlineData("salary received this year", "total", null, null, "2026-01-01", "2026-11-20")]
    public void Keyword_parser_reads_common_questions(string question, string metric, string? category, string? merchant, string from, string to)
    {
        var q = KeywordQuestionParser.Parse(question, Today, Data.Select(t => t.Merchant));
        Assert.Equal((metric, category, merchant, DateOnly.Parse(from), DateOnly.Parse(to)), (q.Metric, q.Category, q.Merchant, q.From!.Value, q.To!.Value));
    }

    [Fact]
    public async Task Ai_budget_caps_calls_per_minute()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var budget = new AiBudget(clock, perMinute: 2, perDay: 10);
        var fake = new FakeChatClient(_ => "{\"metric\":\"total\"}");
        var ask = new AskService(fake, clock, budget: budget);
        var used = new List<bool>();
        for (var i = 0; i < 4; i++) used.Add((await ask.AskAsync("food this month", Data)).UsedAi);
        Assert.Equal([true, true, false, false], used);
        Assert.Equal(2, fake.Prompts.Count);
    }
}
