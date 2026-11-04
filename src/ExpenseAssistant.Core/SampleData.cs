namespace ExpenseAssistant.Core;

/// <summary>
/// Made-up transactions in the shape of real Indian bank alerts (HDFC/SIB style UPI narrations). The demo and the tests
/// use these, so no real account data is ever in the repository or sent to a model.
/// </summary>
public static class SampleData
{
    public static IReadOnlyList<Transaction> Create(DateOnly today, int months = 3, int seed = 42)
    {
        var rnd = new Random(seed);
        var rows = new List<Transaction>();
        var id = 1;
        void Add(DateOnly d, decimal amount, TxKind kind, string merchant, string narration, string category = Categories.Uncategorized)
        {
            if (d <= today) rows.Add(new Transaction(id++, d, amount, kind, merchant, narration, category));
        }

        (string Merchant, string Vpa, decimal Min, decimal Max)[] everyday =
        [
            ("SWIGGY", "swiggy.stores@axisbank", 180, 650), ("ZOMATO", "zomato@paytm", 150, 520),
            ("MURUGAN TEA STALL", "q412345678@ybl", 15, 40), ("BIGBASKET", "bigbasket@hdfcbank", 400, 1800),
            ("UBER INDIA", "uber.rides@icici", 90, 420), ("APOLLO PHARMACY", "apollopharmacy@ybl", 120, 900),
            ("AMAZON PAY", "amazonpay@apl", 300, 2500), ("RAVI KUMAR", "ravikumar1987@okaxis", 200, 2000),
        ];
        for (var m = months - 1; m >= 0; m--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-m);
            Add(first.AddDays(1), 70000, TxKind.Credit, "ACME SOFTWARE PVT LTD", "NEFT CR-HDFC0000001-ACME SOFTWARE PVT LTD-SALARY");
            Add(first.AddDays(4), 15000, TxKind.Debit, "HOUSE RENT", "UPI-LANDLORD NAME-landlord.rent9@oksbi-SBIN0001234-6151234567-RENT");
            Add(first.AddDays(4), 12500, TxKind.Debit, "ICICI LOAN EMI", "ACH D- TP ACH ICICI BANK LTD-LOAN EMI 12345678");
            Add(first.AddDays(6), 349, TxKind.Debit, "JIO PREPAID", "UPI-JIO PREPAID RECHARGES-jio@icici-ICIC0000001-6170000000-RECHARGE");
            Add(first.AddDays(9), 5000, TxKind.Debit, "ZERODHA", "UPI-ZERODHA BROKING-zerodha@hdfcbank-HDFC0000001-6180000000-SIP");
            for (var i = 0; i < 22; i++)
            {
                var (merchant, vpa, min, max) = everyday[rnd.Next(everyday.Length)];
                var amount = Math.Round(min + (decimal)rnd.NextDouble() * (max - min));
                var phone = 9000000000L + rnd.Next(0, 999999999);
                Add(first.AddDays(rnd.Next(0, 28)), amount, TxKind.Debit, merchant,
                    $"UPI-{merchant}-{vpa}-YESB0YBLUPI-{rnd.Next(600000000, 699999999)}{rnd.Next(100, 999)}-PAID FROM {phone}");
            }
        }
        return rows.OrderBy(r => r.Date).ThenBy(r => r.Id).ToList();
    }
}
