using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.Reporting;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Domain.Tests;

public sealed class RefundAccountingTests
{
    [Fact]
    public void RefundsReduceSpendingAcrossBudgetReportsAndBalances()
    {
        var category = Guid.NewGuid();
        var entries = new[] {
            new Transaction { Type = TransactionType.Expense, Amount = 100, CategoryId = category, OccurredOnUtc = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc) },
            new Transaction { Type = TransactionType.Refund, Amount = 30, CategoryId = category, OccurredOnUtc = new DateTime(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc) },
            new Transaction { Type = TransactionType.TransferIn, Amount = 400, OccurredOnUtc = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc) }
        };
        Assert.Equal(330, AccountBalanceCalculator.Calculate(new Account(), entries));
        var buckets = ReportingCalendar.BuildMonthBuckets(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1));
        var cash = Assert.Single(TransactionAggregationCalculator.BuildMonthlyCashFlow(buckets, entries));
        Assert.Equal(0, cash.Income); Assert.Equal(70, cash.Expenses); Assert.Equal(-70, cash.Net);
        Assert.Equal(70, Assert.Single(TransactionAggregationCalculator.BuildMonthlySpending(buckets, entries)).Amount);
        Assert.Equal(70, Assert.Single(CategoryBreakdownCalculator.BuildRankedBreakdown(entries, new Dictionary<Guid, string>())).Amount);
        var period = new BudgetPeriod { Year = 2026, Month = 4, CategoryLimits = [new BudgetCategoryLimit { CategoryId = category, PlannedAmount = 200 }] };
        var budget = BudgetSummaryCalculator.BuildMonthlySummary(period, entries);
        Assert.Equal(70, budget.TotalSpent); Assert.Equal(130, budget.TotalRemaining);
    }

    [Fact]
    public void SplitChildrenDoNotChangeAccountBalanceOrDoubleCountSpending()
    {
        var parent = Guid.NewGuid(); var group = Guid.NewGuid(); var category = Guid.NewGuid();
        var entries = new[] {
            new Transaction { Id = parent, Type = TransactionType.Expense, Amount = 100, SplitGroupId = group, OccurredOnUtc = new DateTime(2026,4,10) },
            new Transaction { ParentTransactionId = parent, SplitGroupId = group, Type = TransactionType.Expense, Amount = 100, CategoryId = category, OccurredOnUtc = new DateTime(2026,4,10) },
            new Transaction { Type = TransactionType.Refund, Amount = 25, CategoryId = category, OccurredOnUtc = new DateTime(2026,4,11) }
        };
        Assert.Equal(-75, AccountBalanceCalculator.Calculate(new Account(), entries));
        Assert.Equal(75, Assert.Single(CategoryBreakdownCalculator.BuildRankedBreakdown(entries, new Dictionary<Guid, string>())).Amount);
    }
}
