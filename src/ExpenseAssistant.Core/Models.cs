namespace ExpenseAssistant.Core;

public enum TxKind { Debit, Credit }

/// <summary>One bank transaction. <see cref="Narration"/> is the raw bank text (it can hold UPI ids and phone numbers).</summary>
public sealed record Transaction(int Id, DateOnly Date, decimal Amount, TxKind Kind, string Merchant, string Narration, string Category = Categories.Uncategorized);

public static class Categories
{
    public const string Uncategorized = "Other";

    /// <summary>The only labels the app accepts; an AI answer outside this list is ignored.</summary>
    public static readonly IReadOnlyList<string> All =
        ["Food", "Groceries", "Transport", "Shopping", "Bills", "Entertainment", "Health", "Rent & EMI", "Investments", "Family", "Transfers", "Other"];

    /// <summary>Money moved, not spent: left out of spending totals.</summary>
    public static readonly IReadOnlySet<string> NonSpend = new HashSet<string>(["Transfers", "Investments"]);

    public static bool IsKnown(string? c) => c is not null && All.Contains(c);

    public static string? Normalize(string? c) => All.FirstOrDefault(x => string.Equals(x, c?.Trim(), StringComparison.OrdinalIgnoreCase));
}
