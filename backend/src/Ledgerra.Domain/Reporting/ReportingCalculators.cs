using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Transactions;

namespace Ledgerra.Domain.Reporting;

public sealed record MonthBucket(DateOnly MonthStart, string Label);

public sealed record MonthlySpendingPoint(string Month, decimal Amount);

public sealed record MonthlyCashFlowPoint(string Month, decimal Income, decimal Expenses, decimal Net);

public sealed record CategoryBreakdownPoint(Guid CategoryId, string CategoryName, decimal Amount, decimal Percentage);

public sealed record NetWorthPoint(string Month, decimal NetWorth, string CurrencyCode);

public static class ReportingCalendar
{
    public static IReadOnlyList<MonthBucket> BuildMonthBuckets(DateOnly startMonth, DateOnly endMonth)
    {
        var buckets = new List<MonthBucket>();
        var cursor = new DateOnly(startMonth.Year, startMonth.Month, 1);
        var last = new DateOnly(endMonth.Year, endMonth.Month, 1);

        while (cursor <= last)
        {
            buckets.Add(new MonthBucket(cursor, FormatMonth(cursor)));
            cursor = cursor.AddMonths(1);
        }

        return buckets;
    }

    public static string FormatMonth(DateOnly month) => $"{month.Year:D4}-{month.Month:D2}";

    public static DateOnly MonthEnd(DateOnly month)
    {
        var start = new DateOnly(month.Year, month.Month, 1);
        return start.AddMonths(1).AddDays(-1);
    }
}

public static class TransactionAggregationCalculator
{
    public static IReadOnlyList<MonthlySpendingPoint> BuildMonthlySpending(
        IReadOnlyList<MonthBucket> buckets,
        IEnumerable<Transaction> transactions)
    {
        var expensesByMonth = transactions
            .Where(IsRootExpense)
            .GroupBy(item => new DateOnly(item.OccurredOnUtc.Year, item.OccurredOnUtc.Month, 1))
            .ToDictionary(group => group.Key, group => group.Sum(TransactionSemantics.SpendingAmount));

        return buckets
            .Select(bucket => new MonthlySpendingPoint(bucket.Label, expensesByMonth.GetValueOrDefault(bucket.MonthStart)))
            .ToList();
    }

    public static IReadOnlyList<MonthlyCashFlowPoint> BuildMonthlyCashFlow(
        IReadOnlyList<MonthBucket> buckets,
        IEnumerable<Transaction> transactions)
    {
        var cashFlowByMonth = transactions
            .Where(item => !item.ParentTransactionId.HasValue && item.Type is TransactionType.Income or TransactionType.Expense or TransactionType.Refund)
            .GroupBy(item => new DateOnly(item.OccurredOnUtc.Year, item.OccurredOnUtc.Month, 1))
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Income = group.Where(item => item.Type == TransactionType.Income).Sum(item => item.Amount),
                    Expenses = group.Where(TransactionSemantics.IsSpending).Sum(TransactionSemantics.SpendingAmount)
                });

        return buckets
            .Select(bucket =>
            {
                var totals = cashFlowByMonth.GetValueOrDefault(bucket.MonthStart);
                var income = totals?.Income ?? 0m;
                var expenses = totals?.Expenses ?? 0m;
                return new MonthlyCashFlowPoint(bucket.Label, income, expenses, income - expenses);
            })
            .ToList();
    }

    private static bool IsRootExpense(Transaction transaction)
    {
        return TransactionSemantics.IsSpending(transaction) && !transaction.ParentTransactionId.HasValue;
    }
}

public static class CategoryBreakdownCalculator
{
    public static IReadOnlyList<CategoryBreakdownPoint> BuildRankedBreakdown(
        IEnumerable<Transaction> transactions,
        IReadOnlyDictionary<Guid, string> categoryNames)
    {
        var totals = transactions
            .Where(IsCategoryExpense)
            .GroupBy(item => item.CategoryId!.Value)
            .Select(group => new
            {
                CategoryId = group.Key,
                CategoryName = categoryNames.GetValueOrDefault(group.Key, "Uncategorized"),
                Amount = group.Sum(TransactionSemantics.SpendingAmount)
            })
            .OrderByDescending(item => item.Amount)
            .ToList();

        var totalAmount = totals.Sum(item => item.Amount);

        return totals
            .Select(item => new CategoryBreakdownPoint(
                item.CategoryId,
                item.CategoryName,
                item.Amount,
                totalAmount == 0m ? 0m : Math.Round(item.Amount / totalAmount * 100m, 2)))
            .ToList();
    }

    private static bool IsCategoryExpense(Transaction transaction)
    {
        return TransactionSemantics.IsSpending(transaction) &&
            transaction.CategoryId.HasValue &&
            (transaction.ParentTransactionId.HasValue || !transaction.SplitGroupId.HasValue);
    }
}

public static class NetWorthRollupCalculator
{
    public static IReadOnlyList<NetWorthPoint> BuildNetWorthHistory(
        IReadOnlyList<MonthBucket> buckets,
        IEnumerable<MonthlyAccountBalanceSnapshot> snapshots,
        string currencyCode)
    {
        var totalsByMonth = snapshots
            .GroupBy(item => item.MonthEndDate)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Balance));

        return buckets
            .Select(bucket => new NetWorthPoint(
                bucket.Label,
                totalsByMonth.GetValueOrDefault(ReportingCalendar.MonthEnd(bucket.MonthStart)),
                currencyCode))
            .ToList();
    }
}
