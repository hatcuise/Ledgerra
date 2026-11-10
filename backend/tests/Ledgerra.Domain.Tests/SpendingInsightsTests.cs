using Ledgerra.Domain.Insights;
namespace Ledgerra.Domain.Tests;
public sealed class SpendingInsightsTests
{
    private static readonly Guid Food = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static InsightEntry Row(decimal amount, string date, string type = "Expense", Guid? category = null) => new(Guid.NewGuid(), category ?? Food, amount, type, DateOnly.Parse(date));
    private static SpendingInsights Build(List<InsightEntry> rows, InsightPreferences? prefs = null, List<InsightBudget>? budgets = null, DateOnly? today = null, bool complete = true) =>
        SpendingInsightsCalculator.Build(new DateOnly(2026, 4, 1), today ?? new DateOnly(2026, 4, 15), "USD", rows, budgets ?? [], new Dictionary<Guid, string> { [Food] = "Food", [Home] = "Home" }, new Dictionary<Guid, string>(), prefs ?? new(), complete);

    [Fact]
    public void TransfersRefundsAndSplits_AreCountedOnceAndAllocatedToCategories()
    {
        var parent = Row(100, "2026-04-10") with { CategoryId = null, SplitGroupId = Guid.NewGuid() };
        var rows = new List<InsightEntry> { parent, Row(60, "2026-04-10") with { ParentTransactionId = parent.Id, SplitGroupId = parent.SplitGroupId }, Row(40, "2026-04-10", category: Home) with { ParentTransactionId = parent.Id, SplitGroupId = parent.SplitGroupId }, Row(20, "2026-04-11", "Refund"), Row(200, "2026-04-01", "Income"), Row(1000, "2026-04-12", "TransferIn"), Row(1000, "2026-04-12", "TransferOut") };
        var result = Build(rows, budgets: [new(Food, "Food", 50)]);
        Assert.Equal(80, result.NetSpending); Assert.Equal(120, result.CashFlow); Assert.Equal(80, result.Categories.Sum(x => x.Spent)); Assert.Equal(80, result.Payees.Sum(x => x.Spent));
        Assert.Single(result.Alerts); Assert.Equal("BudgetWarning", result.Alerts[0].Kind); Assert.Equal(40, result.Alerts[0].Amount);
    }
    [Fact]
    public void HistoricalComparison_AlignsDaysAndDoesNotFillMissingMonthsWithZeros()
    {
        var result = Build([Row(100, "2026-01-10"), Row(900, "2026-01-20"), Row(100, "2026-02-10"), Row(900, "2026-02-20"), Row(200, "2026-04-10")]);
        Assert.Equal(2, result.HistoricalMonths); Assert.Equal(100, result.HistoricalAverage); Assert.Equal(100, result.PercentChange); Assert.Null(result.PreviousMonthSpending);
        var alert = Assert.Single(result.Alerts); Assert.Equal("SpendingIncrease", alert.Kind); Assert.Equal(2, alert.SampleCount);
        Assert.Equal(100, result.Daily[14].HistoricalAverage); Assert.Equal(1000, result.Daily[29].HistoricalAverage);
    }
    [Fact]
    public void OneHistoricalMonth_DoesNotClaimAnEstablishedPattern()
    {
        var result = Build([Row(30, "2026-03-01"), Row(300, "2026-04-10")]);
        Assert.Null(result.HistoricalAverage); Assert.DoesNotContain(result.Alerts, x => x.Kind == "SpendingIncrease"); Assert.Equal(1, result.HistoricalMonths);
    }
    [Fact]
    public void BudgetWarningEscalation_HasDistinctKeysAndUsesAvailableCarryover()
    {
        var near = Build([Row(160, "2026-04-10")], budgets: [new(Food, "Food", 200)]).Alerts.Single(x => x.Kind == "BudgetWarning");
        var over = Build([Row(210, "2026-04-10")], budgets: [new(Food, "Food", 200)]).Alerts.Single(x => x.Kind == "BudgetExceeded");
        Assert.NotEqual(near.Key, over.Key); Assert.Equal(200, near.Baseline); Assert.Equal("warning", over.Severity);
        var zero = Build([Row(10, "2026-04-10")], budgets: [new(Food, "Food", 0)]).Alerts.Single(); Assert.Equal("BudgetExceeded", zero.Kind); Assert.Null(zero.PercentChange);
    }
    [Fact]
    public void Pace_RequiresSevenDaysAndDoesNotUseFutureTransactions()
    {
        var rows = new List<InsightEntry> { Row(70, "2026-04-01"), Row(9000, "2026-04-30") };
        var early = Build(rows, today: new(2026, 4, 6)); Assert.Null(early.ProjectedMonthEnd);
        var current = Build(rows, today: new(2026, 4, 7), budgets: [new(Food, "Food", 200)]);
        Assert.Equal(70, current.NetSpending); Assert.Equal(300, current.ProjectedMonthEnd); Assert.Null(current.Daily[7].Current); Assert.Equal(300, current.Daily[^1].Projected);
        Assert.Contains(current.Alerts, x => x.Kind == "BudgetPace");
        var closed = Build(rows, today: new(2026, 5, 1)); Assert.Null(closed.ProjectedMonthEnd); Assert.False(closed.IsCurrentMonth);
        var future = Build(rows, today: new(2026, 3, 20)); Assert.Equal(0, future.NetSpending); Assert.Empty(future.Alerts); Assert.Null(future.ThroughDate);
    }
    [Fact]
    public void UnusualPurchase_UsesMedianAndFiveSamplesAndNeverCallsIncomeUnusual()
    {
        var rows = new List<InsightEntry> { Row(10, "2026-01-01"), Row(20, "2026-02-01"), Row(20, "2026-02-02"), Row(25, "2026-03-01"), Row(1000, "2026-03-02"), Row(150, "2026-04-10"), Row(9000, "2026-04-10", "Income") };
        var alert = Build(rows).Alerts.Single(x => x.Kind == "UnusualPurchase"); Assert.Equal(20, alert.Baseline); Assert.Equal(5, alert.SampleCount); Assert.Equal(150, alert.Amount);
        rows.RemoveAt(0); Assert.DoesNotContain(Build(rows).Alerts, x => x.Kind == "UnusualPurchase");
    }
    [Fact]
    public void AlertPreferences_AreIndependentAndValidated()
    {
        var prefs = new InsightPreferences { BudgetAlerts = false, SpendingChangeAlerts = false, UnusualPurchaseAlerts = false, LargePurchaseAlerts = false };
        Assert.Empty(Build([Row(900, "2026-04-01")], prefs, [new(Food, "Food", 10)]).Alerts);
        Assert.True(prefs.IsValid()); prefs.BudgetWarningPercent = 101; Assert.False(prefs.IsValid()); prefs.BudgetWarningPercent = 80; prefs.LargePurchaseThreshold = .001m; Assert.False(prefs.IsValid());
    }
    [Fact]
    public void MissingFx_PausesMonetaryAlertsComparisonsAndForecasts()
    {
        var result = Build([Row(5000, "2026-04-01"), Row(10, "2026-02-01"), Row(10, "2026-03-01")], budgets: [new(Food, "Food", 10)], complete: false);
        Assert.False(result.DataComplete); Assert.Empty(result.Alerts); Assert.Null(result.ProjectedMonthEnd); Assert.Null(result.HistoricalAverage); Assert.Null(result.Categories[0].BudgetRemaining);
    }
    [Fact]
    public void ShortMonthComparison_ClampsToFebruaryEndAndNegativeRefundTotalsStayNegative()
    {
        var result = Build([Row(200, "2026-02-28"), Row(200, "2026-03-30"), Row(100, "2026-04-01"), Row(200, "2026-04-02", "Refund")], today: new(2026, 4, 30));
        Assert.Equal(200, result.HistoricalAverage); Assert.Equal(-100, result.NetSpending); Assert.Equal(-100, result.Categories[0].Spent); Assert.Null(result.ProjectedMonthEnd); Assert.DoesNotContain(result.Alerts, x => x.Kind == "SpendingIncrease");
    }
}
