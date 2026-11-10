using System.Security.Cryptography;
using System.Text;
namespace Ledgerra.Domain.Insights;

public static class SpendingInsightsCalculator
{
    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static decimal Signed(InsightEntry row) => row.Type == "Refund" ? -row.Amount : row.Amount;
    private static bool Spending(InsightEntry row) => row.Type is "Expense" or "Refund";
    private static bool Allocation(InsightEntry row) => row.ParentTransactionId.HasValue || !row.SplitGroupId.HasValue;
    private static decimal Total(IEnumerable<InsightEntry> entries) => entries.Where(x => Spending(x) && !x.ParentTransactionId.HasValue).Sum(Signed);
    private static decimal? Percent(decimal current, decimal baseline) => baseline > 0 ? Round((current - baseline) / baseline * 100) : null;
    private static DateOnly Start(DateOnly date) => new(date.Year, date.Month, 1);
    private static DateOnly End(DateOnly date) => Start(date).AddMonths(1).AddDays(-1);

    public static SpendingInsights Build(DateOnly month, DateOnly today, string currency, IReadOnlyList<InsightEntry> entries,
        IReadOnlyList<InsightBudget> budgets, IReadOnlyDictionary<Guid, string> categoryNames,
        IReadOnlyDictionary<Guid, string> payeeNames, InsightPreferences preferences,
        bool dataComplete = true, int excludedEntries = 0, IReadOnlyList<string>? warnings = null)
    {
        month = Start(month);
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var elapsed = today < month ? 0 : today >= End(month) ? days : today.Day;
        DateOnly? through = elapsed > 0 ? month.AddDays(elapsed - 1) : null;
        var currentMonth = Start(today) == month;
        var current = entries.Where(x => x.Date >= month && through.HasValue && x.Date <= through).ToList();
        var history = Enumerable.Range(1, 3).Select(i => month.AddMonths(-i))
            .Where(m => entries.Any(x => x.Date >= m && x.Date <= End(m) && x.ParentTransactionId == null && (Spending(x) || x.Type == "Income"))).ToList();
        var comparison = history.Select(m => entries.Where(x => x.Date >= m && x.Date <= m.AddDays(Math.Min(elapsed, DateTime.DaysInMonth(m.Year, m.Month)) - 1)).ToList()).ToList();
        var hasHistory = elapsed > 0 && history.Count >= 2 && dataComplete;
        var net = Round(Total(current));
        var income = Round(current.Where(x => x.Type == "Income" && !x.ParentTransactionId.HasValue).Sum(x => x.Amount));
        decimal? average = hasHistory ? Round(comparison.Average(Total)) : null;
        var previousMonth = month.AddMonths(-1);
        decimal? previous = dataComplete && elapsed > 0 && history.Contains(previousMonth)
            ? Round(Total(entries.Where(x => x.Date >= previousMonth && x.Date <= previousMonth.AddDays(Math.Min(elapsed, DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month)) - 1)))) : null;
        decimal? projection = dataComplete && currentMonth && elapsed >= 7 && elapsed < days && net > 0 ? Round(net / elapsed * days) : null;
        var categoryIds = current.Where(x => Spending(x) && Allocation(x)).Select(x => x.CategoryId).Concat(budgets.Select(x => (Guid?)x.CategoryId)).Distinct();
        var categories = categoryIds.Select(id =>
        {
            var spent = Round(current.Where(x => Spending(x) && Allocation(x) && x.CategoryId == id).Sum(Signed));
            decimal? baseline = hasHistory ? Round(comparison.Average(rows => rows.Where(x => Spending(x) && Allocation(x) && x.CategoryId == id).Sum(Signed))) : null;
            var budget = budgets.SingleOrDefault(x => x.CategoryId == id);
            return new InsightCategory(id, id.HasValue ? categoryNames.GetValueOrDefault(id.Value, "Deleted category") : "Uncategorized", spent,
                baseline, baseline.HasValue ? spent - baseline : null, baseline.HasValue ? Percent(spent, baseline.Value) : null,
                dataComplete ? budget?.Available : null, dataComplete && budget is not null ? budget.Available - spent : null);
        }).OrderByDescending(x => x.Spent).ThenBy(x => x.Name).ToList();
        var payees = current.Where(x => Spending(x) && !x.ParentTransactionId.HasValue).GroupBy(x => x.PayeeId)
            .Select(g => new InsightPayee(g.Key, g.Key.HasValue ? payeeNames.GetValueOrDefault(g.Key.Value, "Deleted payee") : "Unassigned payee", Round(g.Sum(Signed)), g.Count()))
            .OrderByDescending(x => x.Spent).ThenBy(x => x.Name).ToList();
        var alerts = new List<SpendingAlert>();
        void Add(string kind, string severity, string title, string description, decimal amount, decimal? baseline, decimal? percent,
            decimal? projected, Guid? category, Guid? transaction, int samples = 0)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{month:yyyy-MM}|{category}|{transaction}|{currency}")));
            alerts.Add(new SpendingAlert(key, kind, severity, title, description, amount, baseline, percent, projected, category, transaction, samples));
        }
        if (dataComplete && elapsed > 0)
        {
            foreach (var category in categories)
            {
                if (preferences.BudgetAlerts && category.BudgetAvailable is decimal available && category.Spent > 0)
                {
                    var used = available > 0 ? Round(category.Spent / available * 100) : (decimal?)null;
                    var projected = projection.HasValue ? Round(category.Spent / elapsed * days) : (decimal?)null;
                    if (category.Spent > available)
                        Add("BudgetExceeded", "warning", $"{category.Name} is over budget", "Net spending exceeds the available category budget, including carryover.", category.Spent, available, used, projected, category.CategoryId, null);
                    else if (used >= preferences.BudgetWarningPercent)
                        Add("BudgetWarning", "attention", $"{category.Name} is nearing its budget", "This category has reached your budget warning threshold.", category.Spent, available, used, projected, category.CategoryId, null);
                    else if (projected > available && available > 0)
                        Add("BudgetPace", "attention", $"{category.Name} may exceed its budget", "A straight-line estimate at your current daily pace exceeds the category budget. This is an estimate, not a bill forecast.", category.Spent, available, used, projected, category.CategoryId, null);
                }
                if (preferences.SpendingChangeAlerts && category.HistoricalAverage is decimal baseline && baseline >= 0
                    && category.Spent - baseline >= preferences.MinimumSpendingIncrease
                    && (baseline == 0 || category.PercentChange >= preferences.SpendingIncreasePercent))
                    Add("SpendingIncrease", "attention", baseline == 0 ? $"New spending in {category.Name}" : $"Spending is up in {category.Name}",
                        "Compared with the average through the same day of each recorded prior month. Missing months are not treated as zero.", category.Spent, baseline, category.PercentChange, null, category.CategoryId, null, history.Count);
            }
            foreach (var entry in current.Where(x => x.Type == "Expense" && !x.ParentTransactionId.HasValue))
            {
                var samples = entry.CategoryId.HasValue && !entry.SplitGroupId.HasValue
                    ? entries.Where(x => x.Date >= month.AddMonths(-3) && x.Date < month && x.Type == "Expense" && !x.ParentTransactionId.HasValue && !x.SplitGroupId.HasValue && x.CategoryId == entry.CategoryId).Select(x => x.Amount).OrderBy(x => x).ToList() : [];
                decimal? median = samples.Count < 5 ? null : samples.Count % 2 == 1 ? samples[samples.Count / 2] : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2;
                if (preferences.UnusualPurchaseAlerts && median > 0 && entry.Amount >= preferences.UnusualPurchaseMinimum && entry.Amount >= median * preferences.UnusualPurchaseMultiplier)
                    Add("UnusualPurchase", "attention", "A purchase is larger than usual", "Compared with the median of at least five ordinary purchases in the same category over the three prior months. Review the transaction; this is not a fraud determination.", entry.Amount, Round(median.Value), Percent(entry.Amount, median.Value), null, entry.CategoryId, entry.Id, samples.Count);
                else if (preferences.LargePurchaseAlerts && entry.Amount >= preferences.LargePurchaseThreshold)
                    Add("LargePurchase", "attention", "A purchase reached your large-spending threshold", "This posted purchase meets the amount you chose for large-purchase alerts. Transfers and refunds are excluded.", entry.Amount, preferences.LargePurchaseThreshold, null, null, entry.CategoryId, entry.Id);
            }
        }
        var daily = Enumerable.Range(1, days).Select(day =>
        {
            decimal? actual = day <= elapsed ? Round(Total(current.Where(x => x.Date.Day <= day))) : null;
            decimal? historical = hasHistory ? Round(history.Average(m => Total(entries.Where(x => x.Date >= m && x.Date <= m.AddDays(Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month)) - 1))))) : null;
            decimal? projected = projection.HasValue && day >= elapsed ? Round(net / elapsed * day) : null;
            return new InsightDay(day, actual, historical, projected);
        }).ToList();
        return new SpendingInsights($"{month:yyyy-MM}", through, currency, currentMonth, elapsed, days, dataComplete, excludedEntries, history.Count,
            income, net, income - net, previous, average, average.HasValue ? net - average : null,
            average.HasValue ? Percent(net, average.Value) : null, projection, categories, payees, daily,
            alerts.OrderBy(x => x.Severity == "warning" ? 0 : 1).ThenBy(x => x.Kind).ThenByDescending(x => x.Amount).ToList(), warnings ?? []);
    }
}
