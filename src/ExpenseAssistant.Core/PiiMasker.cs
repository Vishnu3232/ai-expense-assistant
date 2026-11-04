using System.Text.RegularExpressions;

namespace ExpenseAssistant.Core;

/// <summary>
/// Removes personal identifiers from bank text before it is sent to an AI model: UPI ids, phone numbers, card and account
/// numbers, and long reference numbers. The merchant words that decide a category ("SWIGGY", "APOLLO PHARMACY") stay.
/// </summary>
public static partial class PiiMasker
{
    // no "-" in the id part: bank narrations use "-" between fields ("UPI-RAVI KUMAR-ravikumar1987@okaxis-...")
    [GeneratedRegex(@"[a-z0-9._]{2,}@[a-z]{2,}", RegexOptions.IgnoreCase)] private static partial Regex UpiId();
    [GeneratedRegex(@"(?<!\d)(?:\+?91[\s-]?)?[6-9]\d{9}(?!\d)")] private static partial Regex Phone();
    [GeneratedRegex(@"(?:x{2,}|\*{2,})\d{2,6}|\b\d{4}[\s-]?x{4}[\s-]?x{4}[\s-]?\d{4}\b", RegexOptions.IgnoreCase)] private static partial Regex MaskedCard();
    [GeneratedRegex(@"\b\d{6,}\b")] private static partial Regex LongNumber();
    [GeneratedRegex(@"\b[A-Z]{4}0[A-Z0-9]{6}\b")] private static partial Regex Ifsc();

    public static string Mask(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = UpiId().Replace(text, "[upi]");
        s = MaskedCard().Replace(s, "[card]");
        s = Phone().Replace(s, "[phone]");
        s = Ifsc().Replace(s, "[ifsc]");
        s = LongNumber().Replace(s, "[num]");
        return s;
    }
}
