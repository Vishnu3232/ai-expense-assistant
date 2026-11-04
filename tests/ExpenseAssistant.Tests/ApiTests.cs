using System.Net.Http.Json;
using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ExpenseAssistant.Tests;

/// <summary>The whole API in memory, with the model swapped for a scripted one (no network, no key).</summary>
public class ApiTests
{
    private static WebApplicationFactory<Program> App(IChatClient? chat) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseSetting("AI:ApiKey", "").ConfigureServices(s =>
        {
            if (chat is null) return;
            s.AddSingleton(chat);
            s.AddSingleton<ICategorizer>(new AiCategorizer(chat));
            s.AddSingleton(sp => new AskService(chat, TimeProvider.System));
        }));

    [Fact]
    public async Task Works_without_ai_using_rules()
    {
        using var app = App(null);
        var http = app.CreateClient();
        var status = await http.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.False(status.GetProperty("ai").GetBoolean());
        var cat = await (await http.PostAsync("/api/categorize", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RuleCategorizer", cat.GetProperty("by").GetString());
        var food = await http.GetFromJsonAsync<List<Transaction>>("/api/transactions?category=Food");
        Assert.NotEmpty(food!);
    }

    [Fact]
    public async Task Home_page_renders_with_sample_data_and_masked_text()
    {
        using var app = App(null);
        var html = await app.CreateClient().GetStringAsync("/");
        Assert.Contains("AI Expense Assistant", html);
        Assert.Contains("SWIGGY", html);
        Assert.Contains("[upi]", html);                      // the "what the AI sees" line is masked
        Assert.DoesNotContain("@okaxis", html);
    }

    [Fact]
    public async Task Ask_is_rate_limited_per_client()
    {
        using var app = App(null);
        var http = app.CreateClient();
        var codes = new List<int>();
        for (var i = 0; i < 12; i++) codes.Add((int)(await http.PostAsJsonAsync("/api/ask", new { question = "food this month" })).StatusCode);
        Assert.Equal(10, codes.Count(c => c == 200));
        Assert.Equal(2, codes.Count(c => c == 429));
    }

    [Fact]
    public async Task Ask_end_to_end_with_a_scripted_model()
    {
        var fake = new FakeChatClient(p => p.Contains("Question:")
            ? "{\"metric\":\"top_merchants\",\"kind\":\"spend\",\"limit\":3}"
            : "[]");
        using var app = App(fake);
        var http = app.CreateClient();
        await http.PostAsync("/api/categorize", null);
        var res = await http.PostAsJsonAsync("/api/ask", new { question = "Where does my money go?" });
        res.EnsureSuccessStatusCode();
        var a = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Top payees", a.GetProperty("answer").GetString());
        Assert.Contains("HOUSE RENT", a.GetProperty("answer").GetString());
    }
}
