namespace Ledgerra.Domain.Insights;

public sealed class InsightPreferences
{
    public Guid UserId { get; set; }
    public bool BudgetAlerts { get; set; } = true;
    public bool SpendingChangeAlerts { get; set; } = true;
    public bool LargePurchaseAlerts { get; set; } = true;
    public bool UnusualPurchaseAlerts { get; set; } = true;
    public decimal BudgetWarningPercent { get; set; } = 80;
    public decimal SpendingIncreasePercent { get; set; } = 30;
    public decimal MinimumSpendingIncrease { get; set; } = 50;
    public decimal LargePurchaseThreshold { get; set; } = 500;
    public decimal UnusualPurchaseMinimum { get; set; } = 100;
    public decimal UnusualPurchaseMultiplier { get; set; } = 3;

    public bool IsValid() => BudgetWarningPercent is >= 1 and <= 100 && SpendingIncreasePercent is >= 1 and <= 1000
        && MinimumSpendingIncrease is >= 0.01m and <= 999999999 && LargePurchaseThreshold is >= 0.01m and <= 999999999
        && UnusualPurchaseMinimum is >= 0.01m and <= 999999999 && UnusualPurchaseMultiplier is >= 1.5m and <= 100
        && new[] { BudgetWarningPercent, SpendingIncreasePercent, MinimumSpendingIncrease, LargePurchaseThreshold, UnusualPurchaseMinimum, UnusualPurchaseMultiplier }.All(x => x == decimal.Round(x, 2));
}
public sealed class InsightDismissal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AlertKey { get; set; } = "";
    public string Month { get; set; } = "";
    public DateTime DismissedAtUtc { get; set; }
}
public sealed record InsightEntry(Guid Id, Guid? CategoryId, decimal Amount, string Type, DateOnly Date,
    Guid? ParentTransactionId = null, Guid? SplitGroupId = null, Guid? PayeeId = null, string? Note = null);
public sealed record InsightBudget(Guid CategoryId, string Name, decimal Available);
public sealed record SpendingAlert(string Key, string Kind, string Severity, string Title, string Description,
    decimal Amount, decimal? Baseline, decimal? PercentChange, decimal? Projected,
    Guid? CategoryId, Guid? TransactionId, int SampleCount, bool Dismissed = false);
public sealed record InsightCategory(Guid? CategoryId, string Name, decimal Spent, decimal? HistoricalAverage,
    decimal? Change, decimal? PercentChange, decimal? BudgetAvailable, decimal? BudgetRemaining);
public sealed record InsightPayee(Guid? PayeeId, string Name, decimal Spent, int Transactions);
public sealed record InsightDay(int Day, decimal? Current, decimal? HistoricalAverage, decimal? Projected);
public sealed record SpendingInsights(string Month, DateOnly? ThroughDate, string CurrencyCode, bool IsCurrentMonth,
    int ElapsedDays, int DaysInMonth, bool DataComplete, int ExcludedEntries, int HistoricalMonths,
    decimal Income, decimal NetSpending, decimal CashFlow, decimal? PreviousMonthSpending,
    decimal? HistoricalAverage, decimal? Change, decimal? PercentChange, decimal? ProjectedMonthEnd,
    IReadOnlyList<InsightCategory> Categories, IReadOnlyList<InsightPayee> Payees,
    IReadOnlyList<InsightDay> Daily, IReadOnlyList<SpendingAlert> Alerts, IReadOnlyList<string> Warnings, string TimeZone = "UTC");
